using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class BuildCommandParserTests
{
    [Theory]
    [InlineData("dotnet build", "build", ".")]
    [InlineData("dotnet build Foo/Foo.csproj", "build", "Foo/Foo.csproj")]
    [InlineData("dotnet build \"My App\\My App.csproj\" --nologo", "build", "My App\\My App.csproj")]
    [InlineData("dotnet build Foo.csproj --no-incremental", "rebuild", "Foo.csproj")]
    [InlineData("dotnet clean Foo.csproj", "clean", "Foo.csproj")]
    [InlineData("dotnet build -v q Foo.csproj", "build", "Foo.csproj")]
    [InlineData("dotnet build Foo.sln -v:minimal", "build", "Foo.sln")]
    [InlineData("msbuild Foo.csproj /t:Rebuild", "rebuild", "Foo.csproj")]
    [InlineData("msbuild.exe Foo.csproj -t:Clean", "clean", "Foo.csproj")]
    [InlineData("msbuild Foo.csproj /t:Clean,Build /m", "rebuild", "Foo.csproj")]
    [InlineData("\"C:/BuildTools/MSBuild.exe\" Foo.sln /nologo", "build", "Foo.sln")]
    [InlineData("dotnet msbuild Foo.csproj -restore", "build", "Foo.csproj")]
    public void Maps_plain_builds(string command, string action, string project)
    {
        Assert.True(BuildCommandParser.TryParse(command, "C:/repo", out var spec));
        Assert.Equal(action, spec.Action);
        Assert.Equal(new[] { project }, spec.Projects);
        Assert.True(spec.FromShell);
        Assert.Equal("C:/repo", spec.WorkingDirectory);
    }

    [Theory]
    [InlineData("dotnet build -c Release", "Release")]
    [InlineData("dotnet build --configuration:Release", "Release")]
    [InlineData("dotnet build -p:Configuration=Release", "Release")]
    [InlineData("msbuild Foo.csproj /p:Configuration=Release", "Release")]
    public void Keeps_the_configuration(string command, string config)
    {
        Assert.True(BuildCommandParser.TryParse(command, null, out var spec));
        Assert.Equal(config, spec.Configuration);
    }

    [Theory]
    [InlineData("dotnet build && dotnet test")]
    [InlineData("dotnet build | tail")]
    [InlineData("dotnet build; ls")]
    [InlineData("dotnet build > out.txt")]
    [InlineData("dotnet build $env:PROJ")]
    [InlineData("dotnet test")]
    [InlineData("dotnet publish")]
    [InlineData("dotnet build -o out")]
    [InlineData("dotnet build -r win-x64")]
    [InlineData("dotnet build -f net8.0")]
    [InlineData("dotnet build -p:Platform=x64")]
    [InlineData("msbuild Foo.csproj /t:Pack")]
    [InlineData("msbuild Foo.csproj /t:Build;Publish")]
    [InlineData("dotnet build A.csproj B.csproj")]
    [InlineData("dotnet build \"unterminated")]
    [InlineData("git status")]
    [InlineData("")]
    [InlineData(null)]
    public void Leaves_anything_else_to_the_shell(string? command)
    {
        Assert.False(BuildCommandParser.TryParse(command, null, out _));
    }
}

public class VsBridgeTests
{
    [Fact]
    public void Build_spec_round_trips()
    {
        var text = VsBridge.Encode(new VsBuildSpec
        {
            Action = "rebuild",
            Projects = { "C:\\repo\\A\\A.csproj", "B" },
            Configuration = "Release",
            WorkingDirectory = "C:\\repo",
            FromShell = true
        });

        Assert.True(VsBridge.IsBridgeRequest(text));
        Assert.True(VsBridge.TryDecode(text, out VsBuildSpec spec));
        Assert.Equal("rebuild", spec.Action);
        Assert.Equal(new[] { "C:\\repo\\A\\A.csproj", "B" }, spec.Projects);
        Assert.Equal("Release", spec.Configuration);
        Assert.True(spec.FromShell);
        Assert.False(VsBridge.TryDecode(text, out VsTestSpec _));
    }

    [Fact]
    public void Test_spec_round_trips()
    {
        var text = VsBridge.Encode(new VsTestSpec { Projects = { "Tests" }, Filter = "Name=A|Name=B", Build = false });

        Assert.True(VsBridge.TryDecode(text, out VsTestSpec spec));
        Assert.Equal(new[] { "Tests" }, spec.Projects);
        Assert.Equal("Name=A|Name=B", spec.Filter);
        Assert.False(spec.Build);
        Assert.False(VsBridge.TryDecode(text, out VsBuildSpec _));
    }

    [Theory]
    [InlineData("C:\\repo\\File.cs")]
    [InlineData("@build:{not json")]
    [InlineData(null)]
    public void Rejects_non_bridge_text(string? text)
    {
        Assert.False(VsBridge.TryDecode(text, out VsBuildSpec _));
    }
}
