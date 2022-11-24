# MSBuild Logger for export build database

### Usage

Download `msbuild-database.zip` archive from [release page](https://github.com/UnitTestBot/msbuild-database/releases)

Unzip to folder

Add path to `msbuild-database.dll` as logger

Example `msbuild -logger:/path/to/msbuild-database.dll /t:Rebuild MyProject `

 * `/t:Rebuild` - to recompile the entire project and get a list of all compilation commands

### Building

`dotnet build`

Based on [Andrew Baumann - MsBuildCompileCommandsJson ](https://github.com/0xabu/MsBuildCompileCommandsJson)