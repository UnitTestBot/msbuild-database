# MSBuild Logger for export build database 

### Usage

`msbuild -logger:/path/to/msbuild-database.dll /t:Rebuild MyProject `

`/t:Rebuild` - to recompile the entire project and get a list of all compilation commands 

### Building

`dotnet build`


Based on [Andrew Baumann - MsBuildCompileCommandsJson ](https://github.com/0xabu/MsBuildCompileCommandsJson)