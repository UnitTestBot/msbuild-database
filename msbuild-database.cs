using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Web;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// An MsBuild logger that emit compile_commands.json and link_commands.json files from a C++ project build.
/// </summary>
/// <remarks>
/// Based on https://github.com/0xabu/MsBuildCompileCommandsJson
/// </remarks>
public class CompileDatabase : Logger
{
    public override void Initialize(IEventSource eventSource)
    {
        string CompileOutputFilePath = "compile_commands.json";
        string LinkOutputFilePath = "link_commands.json";

        try
        {
            const bool append = false;
            Encoding utf8WithoutBom = new UTF8Encoding(false);
            this.CompileStreamWriter = new StreamWriter(CompileOutputFilePath, append, utf8WithoutBom);
            this.firstLine = true;
            CompileStreamWriter.WriteLine("[");

            this.LinkStreamWriter = new StreamWriter(LinkOutputFilePath, append, utf8WithoutBom);
            LinkStreamWriter.WriteLine("[");
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException
                || ex is ArgumentNullException
                || ex is PathTooLongException
                || ex is DirectoryNotFoundException
                || ex is NotSupportedException
                || ex is ArgumentException
                || ex is SecurityException
                || ex is IOException)
            {
                throw new LoggerException("Failed to create .json files: " + ex.Message);
            }
            else
            {
                // Unexpected failure
                throw;
            }
        }

        eventSource.AnyEventRaised += EventSource_AnyEventRaised;
    }

    private void EventSource_AnyEventRaised(object sender, BuildEventArgs args)
    {
        if (args is TaskCommandLineEventArgs taskArgs)
        {
            const string clExe = ".exe ";
            int clExeIndex = taskArgs.CommandLine.IndexOf(clExe);
            if (clExeIndex == -1)
            {
                throw new LoggerException("Unexpected lack of executable in " + taskArgs.CommandLine);
            }

            string exePath = taskArgs.CommandLine.Substring(0, clExeIndex + clExe.Length - 1);
            string argsString = taskArgs.CommandLine.Substring(clExeIndex + clExe.Length).TrimStart();
            string[] cmdArgs = CommandLineToArgs(argsString);
            string dirname = Path.GetDirectoryName(taskArgs.ProjectFile);
            String taskName = taskArgs.TaskName.ToLowerInvariant();

            if (taskName == "cl")
            {
                bool isLink = false;
                foreach (String arg in cmdArgs)
                {
                    if (arg.Substring(1).ToLowerInvariant() == "link")
                    {
                        isLink = true;
                        break;
                    }
                }

                if (isLink)
                {
                    ProcessLinkCommand(exePath, cmdArgs, dirname);
                }
                else
                {
                    ProcessCompileCommand(exePath, cmdArgs, dirname);
                }
            }
            else if (taskName == "link" || taskArgs.TaskName == "lib")
            {
                ProcessLinkCommand(exePath, cmdArgs, dirname);
            }
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    static string[] CommandLineToArgs(string commandLine)
    {
        commandLine = commandLine.Replace("\r\n", " ");
        int argc;
        var argv = CommandLineToArgvW(commandLine, out argc);
        if (argv == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var args = new string[argc];
            for (var i = 0; i < args.Length; i++)
            {
                var p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(p);
            }

            return args;
        }
        finally
        {
            Marshal.FreeHGlobal(argv);
        }
    }

    private void ProcessCompileCommand(string compilerPath, string[] cmdArgs, String dirname)
    {
        // Options that consume the following argument.
        string[] optionsWithParam =
        {
            "D", "I", "F", "U", "FI", "FU",
            "analyze:log", "analyze:stacksize", "analyze:max_paths",
            "analyze:ruleset", "analyze:plugin"
        };

        List<string> maybeFilenames = new List<string>();
        List<string> filenames = new List<string>();
        bool allFilenamesAreSources = false;

        for (int i = 0; i < cmdArgs.Length; i++)
        {
            bool isOption = cmdArgs[i].StartsWith("/") || cmdArgs[i].StartsWith("-");
            string option = isOption ? cmdArgs[i].Substring(1) : "";

            if (isOption && Array.Exists(optionsWithParam, e => e == option))
            {
                i++; // skip next arg
            }
            else if (option == "Tc" || option == "Tp")
            {
                // next arg is definitely a source file
                if (i + 1 < cmdArgs.Length)
                {
                    filenames.Add(cmdArgs[i + 1]);
                }
            }
            else if (option.StartsWith("Tc") || option.StartsWith("Tp"))
            {
                // rest of this arg is definitely a source file
                filenames.Add(option.Substring(2));
            }
            else if (option == "TC" || option == "TP")
            {
                // all inputs are treated as source files
                allFilenamesAreSources = true;
            }
            else if (option == "link")
            {
                break; // only linker options follow
            }
            else if (isOption || cmdArgs[i].StartsWith("@"))
            {
                // other argument, ignore it
            }
            else
            {
                // non-argument, add it to our list of potential sources
                maybeFilenames.Add(cmdArgs[i]);
            }
        }

        // Iterate over potential sources, and decide (based on the filename)
        // whether they are source inputs.
        foreach (string filename in maybeFilenames)
        {
            if (allFilenamesAreSources)
            {
                filenames.Add(filename);
            }
            else
            {
                int suffixPos = filename.LastIndexOf('.');
                if (suffixPos != -1)
                {
                    string ext = filename.Substring(suffixPos + 1).ToLowerInvariant();
                    if (ext == "c" || ext == "cxx" || ext == "cpp")
                    {
                        filenames.Add(filename);
                    }
                }
            }
        }

        // simplify the compile command to avoid .. etc.
        string compileCommand = '"' + Path.GetFullPath(compilerPath) + "\" " + String.Join(" ", cmdArgs);

        WriteCompileCommand(compileCommand, filenames, dirname);
    }

    private void ProcessLinkCommand(string compilerPath, string[] cmdArgs, String dirname)
    {
        // Options that consume the following argument.
        string[] optionsWithParam =
        {
            "D", "I", "F", "U", "FI", "FU",
            "analyze:log", "analyze:stacksize", "analyze:max_paths",
            "analyze:ruleset", "analyze:plugin"
        };

        List<string> maybeFilenames = new List<string>();
        List<string> filenames = new List<string>();

        for (int i = 0; i < cmdArgs.Length; i++)
        {
            bool isOption = cmdArgs[i].StartsWith("/") || cmdArgs[i].StartsWith("-");
            string option = isOption ? cmdArgs[i].Substring(1) : "";

            if (isOption && Array.Exists(optionsWithParam, e => e == option))
            {
                i++; // skip next arg
            }
            else if (isOption || cmdArgs[i].StartsWith("@"))
            {
                // other argument, ignore it
            }
            else
            {
                // non-argument, add it to our list of potential sources
                maybeFilenames.Add(cmdArgs[i]);
            }
        }

        // Iterate over potential sources, and decide (based on the filename)
        // whether they are source inputs.
        foreach (string filename in maybeFilenames)
        {
            int suffixPos = filename.LastIndexOf('.');
            if (suffixPos != -1)
            {
                string ext = filename.Substring(suffixPos + 1).ToLowerInvariant();
                if (ext == "obj" || ext == "lib" || ext == "dll")
                {
                    filenames.Add(filename);
                }
            }
        }

        // simplify the compile command to avoid .. etc.
        string compileCommand = '"' + Path.GetFullPath(compilerPath) + "\" " + String.Join(" ", cmdArgs);

        WriteLinkCommand(compileCommand, filenames, dirname);
    }

    private void WriteCompileCommand(string compileCommand, List<string> files, string dirname)
    {
        foreach (string filename in files)
        {
            // Terminate the preceding entry
            if (firstLine)
            {
                firstLine = false;
            }
            else
            {
                CompileStreamWriter.WriteLine(",");
            }

            // Write one entry
            CompileStreamWriter.WriteLine("  {");
            CompileStreamWriter.WriteLine(String.Format(
                "    \"command\": \"{0}\",",
                HttpUtility.JavaScriptStringEncode(compileCommand)));
            CompileStreamWriter.WriteLine(String.Format(
                "    \"file\": \"{0}\",",
                HttpUtility.JavaScriptStringEncode(filename)));
            CompileStreamWriter.WriteLine(String.Format(
                "    \"directory\": \"{0}\"",
                HttpUtility.JavaScriptStringEncode(dirname)));
            CompileStreamWriter.Write("  }");
        }
    }

    private void WriteLinkCommand(string linkCommand, List<string> files, string dirname)
    {
        LinkStreamWriter.WriteLine("  {");
        LinkStreamWriter.WriteLine(String.Format(
            "    \"directory\": \"{0}\",",
            HttpUtility.JavaScriptStringEncode(dirname)));
        LinkStreamWriter.WriteLine(String.Format(
            "    \"command\": \"{0}\",",
            HttpUtility.JavaScriptStringEncode(linkCommand)));
        LinkStreamWriter.WriteLine("    \"files\": [");
        bool fl = true;
        foreach (string filename in files)
        {
            if (fl)
            {
                fl = false;
            }
            else
            {
                LinkStreamWriter.WriteLine(",");
            }

            LinkStreamWriter.Write(String.Format("      \"{0}\"",
                HttpUtility.JavaScriptStringEncode(filename)));
        }

        LinkStreamWriter.WriteLine();
        LinkStreamWriter.WriteLine("    ]");
        LinkStreamWriter.WriteLine("  }");
    }

    public override void Shutdown()
    {
        if (!firstLine)
        {
            CompileStreamWriter.WriteLine();
        }

        CompileStreamWriter.WriteLine("]");
        CompileStreamWriter.Close();

        LinkStreamWriter.WriteLine("]");
        LinkStreamWriter.Close();

        base.Shutdown();
    }

    private StreamWriter CompileStreamWriter;
    private StreamWriter LinkStreamWriter;
    private bool firstLine;
}