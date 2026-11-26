using Avalonia.Headless.XUnit;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>"Open in VS Code" of a project's menu: which folder or file it opens, and what it says when that fails.</summary>
public class VsCodeTests
{
    [AvaloniaFact, FreshApp]
    public void The_menu_entry_is_greyed_out_for_a_project_on_a_host()
    {
        // The rail shows the project of the device chosen (this computer): set the host's project as that one.
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var local = projects.AddProject(folder, "local", activate: false);
        projects.AddProject(folder, "remote", "prod", activate: false);
        projects.SetActiveDevice(DeviceKeys.Ssh("prod"));
        var rail = AppHost.Get<Sidebar.ProjectRailViewModel>();

        Assert.False(rail.Chips.Single(c => c.Id != local.Id).OpenInVsCodeCommand.CanExecute(null));

        projects.SetActiveDevice(DeviceKeys.Local);
        Assert.True(AppHost.Get<Sidebar.ProjectRailViewModel>().Chips.Single(c => c.Id == local.Id).OpenInVsCodeCommand.CanExecute(null));
    }

    [AvaloniaFact, FreshApp]
    public void The_command_of_the_settings_page_is_an_empty_setting_when_cleared()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var terminal = AppHost.Get<Octopus.App.Settings.Terminal.TerminalSettingsViewModel>();
        Assert.Equal("", terminal.VsCode);

        terminal.VsCode = "  C:\\Program Files\\Microsoft VS Code\\bin\\code.cmd ";
        terminal.FlushPending();
        Assert.Equal("C:\\Program Files\\Microsoft VS Code\\bin\\code.cmd", settings.Current.Terminal.VsCode);

        terminal.VsCode = "  ";
        terminal.FlushPending();
        Assert.Null(settings.Current.Terminal.VsCode);
    }

    [AvaloniaFact, FreshApp]
    public void A_missing_folder_says_so_and_nothing_starts()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<Sidebar.ProjectRailViewModel>();
        var project = projects.AddProject(folder, "gone");
        Directory.Delete(folder);

        rail.Chips.Single(c => c.Id == project.Id).OpenInVsCodeCommand.Execute(null);

        var dialogs = AppHost.Get<FakeDialogService>();
        // The click hands the work to the UI thread; the dialog comes once it has run.
        Frames.Pump();
        Assert.Equal(1, dialogs.Asked);
        Assert.Equal(folder, dialogs.LastDescription);
    }

    [Fact]
    public void A_project_opens_its_own_folder()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var project = new Project { Path = folder };

        var target = Service(new FakeRunner()).Target(project)!;
        Assert.Equal(Path.GetFullPath(folder), target.Path);
        Assert.Null(target.Folder);
    }

    [Fact]
    public void An_active_branch_workspace_opens_its_worktree()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var worktree = Path.Combine(folder, "feature");
        var workspace = new ProjectWorkspace { Path = worktree };
        var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", WorkspaceId = workspace.Id };
        var project = new Project { Path = folder, Workspaces = [workspace], Items = [agent], ActiveItemId = agent.Id };

        var target = Service(new FakeRunner()).Target(project)!;
        Assert.Equal(Path.GetFullPath(worktree), target.Path);
        Assert.Null(target.Folder);
    }

    [Fact]
    public void An_active_file_opens_as_a_file()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var file = Path.Combine(folder, "Program.cs");
        File.WriteAllText(file, "");
        var item = new ProjectItem { Kind = ProjectItemKind.File, FilePath = file };
        var project = new Project { Path = folder, Items = [item], ActiveItemId = item.Id };

        var target = Service(new FakeRunner()).Target(project)!;
        Assert.Equal(file, target.Path);
        Assert.Null(target.Folder);
    }

    [Fact]
    public async Task The_file_of_a_branch_opens_with_its_worktree()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var workspace = new ProjectWorkspace { Path = Path.Combine(folder, "feature") };
        var file = Path.Combine(folder, "Program.cs");
        File.WriteAllText(file, "");
        var item = new ProjectItem { Kind = ProjectItemKind.File, FilePath = file, WorkspaceId = workspace.Id };
        var project = new Project { Path = folder, Workspaces = [workspace], Items = [item], ActiveItemId = item.Id };
        var runner = new FakeRunner();

        var target = Service(runner).Target(project)!;
        Assert.Equal(file, target.Path);
        Assert.Equal(Path.GetFullPath(workspace.Path), target.Folder);

        Assert.True((await Service(runner).LaunchAsync(target)).Success);
        // The file goes after the folder it belongs to, so git sees the worktree.
        Assert.Equal(
            new[] { "-g", file, "--folder-uri", new Uri(Path.GetFullPath(workspace.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri },
            Assert.Single(runner.Runs).Arguments);
    }

    [Fact]
    public void A_copy_of_a_file_on_a_host_is_not_the_file_the_project_has()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var copy = Path.Combine(folder, "app.log");
        File.WriteAllText(copy, "");
        var item = new ProjectItem { Kind = ProjectItemKind.File, FilePath = copy, RemotePath = "/srv/app/app.log" };
        var project = new Project { Path = folder, Items = [item], ActiveItemId = item.Id };

        Assert.Null(Service(new FakeRunner()).Target(project));
    }

    [Fact]
    public void A_project_on_a_host_has_nothing_to_open_here()
    {
        var project = new Project { Path = "/srv/web", SshHost = "prod" };

        Assert.Null(Service(new FakeRunner()).Target(project));
    }

    [Fact]
    public async Task A_missing_folder_is_reported_and_code_is_not_started()
    {
        var runner = new FakeRunner();
        var missing = Path.Combine(Path.GetTempPath(), "octopus-vscode-missing-" + Guid.NewGuid().ToString("N"));

        var result = await Service(runner).LaunchAsync(new VsCodeTarget(missing));

        Assert.False(result.Success);
        Assert.Equal(missing, result.Error);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task A_bare_code_is_found_the_way_a_shell_finds_it()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var runner = new FakeRunner();
        var codeCmd = Path.Combine(folder, "Microsoft VS Code", "bin", "code.cmd");

        Assert.True((await Service(runner, code: codeCmd).LaunchAsync(new VsCodeTarget(folder))).Success);
        Assert.Equal(codeCmd, Assert.Single(runner.Runs).FileName);
    }

    [Fact]
    public async Task A_code_that_is_not_found_says_how_to_install_it()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var runner = new FakeRunner();

        var result = await Service(runner, code: null).LaunchAsync(new VsCodeTarget(folder));

        Assert.False(result.Success);
        Assert.Contains("code", result.Error);
        Assert.Contains("PATH", result.Error);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task Code_starts_on_the_folder()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var runner = new FakeRunner();
        var settings = new InMemorySettingsStore();

        var result = await Service(runner, settings).LaunchAsync(new VsCodeTarget(folder));

        Assert.True(result.Success);
        var run = Assert.Single(runner.Runs);
        Assert.Equal("code", run.FileName);
        Assert.Equal(new[] { folder }, run.Arguments);
        // The child gets the login PATH: the window's own may not have VS Code.
        Assert.Equal(FakeLogin.Path, run.Environment!["PATH"]);
    }

    [Fact]
    public async Task A_command_of_the_settings_is_used_instead()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var runner = new FakeRunner();
        var settings = new InMemorySettingsStore();
        settings.Update(s => s.Terminal!.VsCode = "/opt/other/code");

        Assert.True((await Service(runner, settings).LaunchAsync(new VsCodeTarget(folder))).Success);
        Assert.Equal("/opt/other/code", Assert.Single(runner.Runs).FileName);
    }

    [Fact]
    public async Task A_folder_code_refuses_is_shown_by_the_running_vs_code()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        // The folder is refused; the command of the settings still starts VS Code, so it is already running.
        var runner = new FakeRunner
        {
            Answer = (_, args) => args.Contains("--version")
                ? new ProcessResult(0, "1.95.0", "", false)
                : new ProcessResult(1, "", "Remote extension host terminated", false),
        };

        var result = await Service(runner).LaunchAsync(new VsCodeTarget(folder));

        Assert.True(result.Success);
        Assert.Equal(["code", "code"], runner.Runs.Select(r => r.FileName));
        Assert.Equal(["--version"], runner.Runs[1].Arguments);
    }

    [Fact]
    public async Task A_code_that_does_not_start_is_reported_with_what_said_so()
    {
        var folder = Directory.CreateTempSubdirectory("octopus-vscode-").FullName;
        var runner = new FakeRunner { StartError = "'code' is not recognized as an internal or external command" };

        var result = await Service(runner).LaunchAsync(new VsCodeTarget(folder));

        Assert.False(result.Success);
        Assert.Equal("'code' is not recognized as an internal or external command", result.Error);
    }

    [Fact]
    public void On_windows_code_runs_as_electron_so_a_window_answers_at_once()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal("1", VsCodeService.EnvironmentForHandoff("run")["ELECTRON_RUN_AS_NODE"]);
        Assert.Equal("run", VsCodeService.EnvironmentForHandoff("run")["OCTOPUS_VSCODE_OBJECT_NAME"]);
    }

    /// <summary>A VS Code service over the fakes; <paramref name="code"/> is what the lookup of a bare command finds (null: nothing).</summary>
    private static VsCodeService Service(IProcessRunner runner, ISettingsStore? settings = null, string? code = "code") =>
        new(runner, settings ?? new InMemorySettingsStore(), new FakeLocator(code), new FakeLogin());

    /// <summary>Finds <paramref name="found"/> for any name; null finds nothing.</summary>
    private sealed class FakeLocator(string? found) : IExecutableLocator
    {
        public IReadOnlyList<string> FindAll(string name, string? pathVariable) => found is null ? [] : [found];
    }

    private sealed class FakeLogin : ILoginEnvironment
    {
        public const string Path = "/fake/login/bin";
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["PATH"] = Path, ["HOME"] = "/home/fake" });
    }

    /// <summary>Records the runs; answers every one with the same exit code, error text and start error, or as <see cref="Answer"/> says.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment)> Runs { get; } = [];
        public int ExitCode { get; set; }
        public string Error { get; set; } = "";
        public string? StartError { get; set; }
        public Func<string, IReadOnlyList<string>, ProcessResult>? Answer { get; set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var args = arguments.ToList();
            Runs.Add((fileName, args, environment));
            if (Answer is not null)
            {
                return Task.FromResult(Answer(fileName, args));
            }
            return Task.FromResult(StartError is not null
                ? new ProcessResult(-1, "", StartError, false) { StartError = StartError }
                : new ProcessResult(ExitCode, "", Error, false));
        }
    }
}
