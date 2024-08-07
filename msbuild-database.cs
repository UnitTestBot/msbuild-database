﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Text.Json;


/// <summary>
/// An MsBuild logger that emit compile_commands.json and link_commands.json files from a C++ project build.
/// </summary>
/// <remarks>
/// Based on https://github.com/0xabu/MsBuildCompileCommandsJson
/// </remarks>
public class CompileDatabase : Logger
{
    private class CompileCommand
    {
        public String command { get; set; }
        public String directory { get; set; }
        public String file { get; set; }
    }

    private class LinkCommand
    {
        public String command { get; set; }
        public String directory { get; set; }
        public List<String> files { get; set; }
    }

    public override void Initialize(IEventSource eventSource)
    {
        string compileOutputFilePath = "compile_commands.json";
        string linkOutputFilePath = "link_commands.json";

        try
        {
            const bool append = false;
            Encoding utf8WithoutBom = new UTF8Encoding(false);
            this.CompileStreamWriter = new StreamWriter(compileOutputFilePath, append, utf8WithoutBom);
            this.LinkStreamWriter = new StreamWriter(linkOutputFilePath, append, utf8WithoutBom);

            compileCommands = new List<CompileCommand>();
            linkCommands = new List<LinkCommand>();
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
            string taskName = taskArgs.TaskName.ToLowerInvariant();
            if (taskName != "cl" && taskName != "link" && taskName != "lib")
            {
                return;
            }

            int clExeIndex = 0;
            string exePath;
            if (taskArgs.CommandLine.Length > 0 && taskArgs.CommandLine[0] == '"')
            {
                bool isEscaped = false;
                clExeIndex++;
                while (clExeIndex < taskArgs.CommandLine.Length)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (taskArgs.CommandLine[clExeIndex] == '"')
                    {
                        break;
                    }
                    else if (taskArgs.CommandLine[clExeIndex] == '\\')
                    {
                        isEscaped = true;
                    }

                    clExeIndex++;
                }

                clExeIndex++;
                exePath = Regex.Unescape(taskArgs.CommandLine.Substring(0, clExeIndex));
            }
            else
            {
                const string dotExe = ".exe";
                clExeIndex = taskArgs.CommandLine.IndexOf(dotExe, StringComparison.OrdinalIgnoreCase) + dotExe.Length;
                if (clExeIndex == -1)
                {
                    Console.WriteLine("Unexpected lack of executable in " + taskArgs.CommandLine);
                    return;
                }

                exePath = taskArgs.CommandLine.Substring(0, clExeIndex);
            }

            string argsString = taskArgs.CommandLine.Substring(clExeIndex + 1).TrimStart();
            string[] cmdArgs = CommandLineToArgs(argsString);
            string dirname = Path.GetDirectoryName(taskArgs.ProjectFile);

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
            else if (taskName == "link" || taskName == "lib")
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
        string compileCommand = Path.GetFullPath(compilerPath) + " " + String.Join(" ", cmdArgs);

        addCompileCommand(compileCommand, filenames, dirname);
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
        string linkCommand = Path.GetFullPath(compilerPath) + " " + String.Join(" ", cmdArgs);

        addLinkCommand(linkCommand, filenames, dirname);
    }

    private void addCompileCommand(string compileCommand, List<string> files, string dirname)
    {
        foreach (string filename in files)
        {
            CompileCommand commandVal = new CompileCommand
            {
                command = compileCommand,
                directory = dirname,
                file = filename
            };

            compileCommands.Add(commandVal);
        }
    }

    private void addLinkCommand(string linkCommand, List<string> files, string dirname)
    {
        LinkCommand commandVal = new LinkCommand()
        {
            command = linkCommand,
            directory = dirname,
            files = files
        };

        linkCommands.Add(commandVal);
    }

    public override void Shutdown()
    {
        JsonSerializerOptions jsonSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        CompileStreamWriter.WriteLine(JsonSerializer.Serialize(compileCommands, jsonSerializerOptions));
        CompileStreamWriter.Close();


        LinkStreamWriter.WriteLine(JsonSerializer.Serialize(linkCommands, jsonSerializerOptions));
        LinkStreamWriter.Close();

        base.Shutdown();
    }


    private List<CompileCommand> compileCommands;
    private List<LinkCommand> linkCommands;
    private StreamWriter CompileStreamWriter;
    private StreamWriter LinkStreamWriter;
}