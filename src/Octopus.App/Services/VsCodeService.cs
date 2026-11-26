using Octopus.App.Localization;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.App.Services;

/// <summary>Where a project opens in VS Code.</summary>
/// <param name="Path">The folder or file VS Code opens.</param>
/// <param name="Folder">The folder of a file when it is a branch's worktree: VS Code opens the file with it, so git knows the worktree. Null: the file's own folder, or none for a folder.</param>
public sealed record VsCodeTarget(string Path, string? Folder = null);

/// <summary>Opens a project's folder (or the open file of it) in VS Code; a report instead of an exception when it cannot.</summary>
public interface IVsCodeService
{
    /// <summary>
    /// The folder to open: the file of an active file tab, the workspace of the active tab (the Git worktree a branch
    /// runs in), or else the project folder. Null when the project is not on this computer, or when the active tab shows
    /// something that is not a path of this computer (the copy of a file on a host).
    /// </summary>
    VsCodeTarget? Target(Project project);

    /// <summary>Starts VS Code on the folder or file; false with a description of what went wrong.</summary>
    Task<VsCodeLaunchResult> LaunchAsync(VsCodeTarget target, CancellationToken cancellationToken = default);
}

public sealed record VsCodeLaunchResult(bool Success, string? Error = null)
{
    public static readonly VsCodeLaunchResult Launched = new(true);
    public static VsCodeLaunchResult Failed(string error) => new(false, error);
}

/// <summary>
/// Starts VS Code with <c>code &lt;path&gt;</c>, the command its installer puts in PATH (or the command of the
/// settings). The command is found the way a shell finds it, in the login PATH (a window launched from the Dock or
/// the Start menu has an old one) and with PATHEXT on Windows: the installer puts <c>code.cmd</c> there, never a
/// <c>code.exe</c>. A command that refuses the folder is followed by <c>code --version</c> of the same command: when a
/// VS Code starts from it, VS Code was running and the folder is shown in its window instead.
/// </summary>
public sealed class VsCodeService(
    IProcessRunner runner,
    ISettingsStore settings,
    IExecutableLocator? locator = null,
    ILoginEnvironment? login = null) : IVsCodeService
{
    internal const string DefaultCommand = "code";

    /// <summary>How long a start may keep running and still count: launching VS Code takes a moment.</summary>
    internal static readonly TimeSpan RunningCountsAsStarted = TimeSpan.FromSeconds(5);

    public VsCodeTarget? Target(Project project)
    {
        // The host's folder is not on this computer: code would open a folder of the same name here, or none at all.
        if (!ProjectLocation.IsOnThisComputer(project))
        {
            return null;
        }
        if (project.ActiveItemId is not { } activeId
            || project.Items.FirstOrDefault(i => i.Id == activeId) is not { } active)
        {
            return new(project.Path);
        }
        if (active.Kind == ProjectItemKind.File && active.FilePath is { Length: > 0 } file)
        {
            // The worktree of the branch the file belongs to, so VS Code understands git worktrees and shows the file.
            var worktree = project.Workspaces.FirstOrDefault(w => w.Id == active.WorkspaceId);
            return active.RemotePath is null ? new(file, worktree is { Path.Length: > 0 } ? worktree.Path : null) : null;
        }
        // The item runs in a branch's worktree: that is the folder to work in.
        var workspace = project.Workspaces.FirstOrDefault(w => w.Id == active.WorkspaceId);
        return new(workspace is { Path.Length: > 0 } ? workspace.Path : project.Path);
    }

    public async Task<VsCodeLaunchResult> LaunchAsync(VsCodeTarget target, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(target.Path) && !Directory.Exists(target.Path))
        {
            return VsCodeLaunchResult.Failed(target.Path);
        }
        var command = settings.Current.Terminal?.VsCode ?? DefaultCommand;
        var environment = await LoginEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executable = Resolve(command, environment);
        if (executable is null)
        {
            return VsCodeLaunchResult.Failed(AppLangResources.Format(AppLangResourceKind.OpenInVsCodeMissing, command));
        }
        var started = await OpenAsync(executable, target.Path, target.Folder, environment, cancellationToken).ConfigureAwait(false);
        if (started.Succeeded)
        {
            return VsCodeLaunchResult.Launched;
        }
        // code may have refused this folder only (a remote that is gone); a VS Code that starts from the command is
        // asked for its window instead.
        var version = await runner.RunAsync(
            executable,
            ["--version"],
            EnvironmentForHandoff(NameOfRun(), environment),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return version.Succeeded ? VsCodeLaunchResult.Launched : VsCodeLaunchResult.Failed(Describe(started));
    }

    /// <summary>
    /// The file to run: a path or a file name with an extension is used as typed; a bare name is looked up in the login
    /// PATH (with every PATHEXT extension too on Windows, where VS Code's <c>code</c> is <c>code.cmd</c>). Null: not
    /// found there.
    /// </summary>
    internal string? Resolve(string command, IReadOnlyDictionary<string, string>? login)
    {
        if (locator is null || Path.GetFileName(command) != command || Path.HasExtension(command))
        {
            return command;
        }
        string? path = null;
        login?.TryGetValue("PATH", out path);
        return locator.FindAll(command, path).FirstOrDefault();
    }

    /// <summary>The login PATH only: a window started from the Dock or the Start menu may have an old one, without VS Code.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> LoginEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (login is null)
        {
            return null;
        }
        var environment = await login.GetAsync(cancellationToken).ConfigureAwait(false);
        return environment.TryGetValue("PATH", out var path) && path.Length > 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = path }
            : null;
    }

    private async Task<ProcessResult> OpenAsync(
        string executable,
        string path,
        string? folder,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        // See NameOfRun: a worktree is opened with its folder. The file goes after it, never both in one start, so
        // code.cmd's pipe never waits on a window it has to walk to before the process behind it can write its answer.
        var arguments = folder is null ? new[] { path } : FolderThenFile(folder, path);
        var run = runner.RunAsync(
            executable, arguments, EnvironmentForHandoff(NameOfRun(), environment), timeout: null, cancellationToken: cancellationToken);
        if (await Task.WhenAny(run, Task.Delay(RunningCountsAsStarted, CancellationToken.None)).ConfigureAwait(false) != run)
        {
            // Still running after a while: it started (a failure to start is reported at once, never after a while).
            return new ProcessResult(0, "", "", false);
        }
        return await run.ConfigureAwait(false);
    }

    /// <summary>The arguments of a start that opens the file of a folder (its worktree): the folder first, then the file.</summary>
    internal static string[] FolderThenFile(string folder, string file) => ["-g", file, "--folder-uri", UriFromPath(folder)];

    private static string UriFromPath(string folder) =>
        new Uri(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;

    /// <summary>The name of one run's mail slot: a run of code.cmd waits for everyone under the same name, and the name is Windows' alone.</summary>
    internal static string NameOfRun() => OperatingSystem.IsWindows() ? Guid.NewGuid().ToString("N") : "";

    /// <summary>
    /// How a run of code.cmd reaches the window's VS Code on Windows, where <paramref name="objectName"/> is the name of
    /// this run's <c>code</c> mail slot, so a window running its own code does not hold the run: Electron starts the
    /// request, <c>ELECTRON_RUN_AS_NODE=1</c> tells the window's Electron it comes from a script and hands it over, and
    /// that window answers at once. Elsewhere the command talks to VS Code itself. Null: the inherited environment.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? EnvironmentForHandoff(string objectName, IReadOnlyDictionary<string, string>? login = null)
    {
        var handoff = OperatingSystem.IsWindows()
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ELECTRON_RUN_AS_NODE"] = "1",
                ["OCTOPUS_VSCODE_OBJECT_NAME"] = objectName,
            }
            : null;
        if (handoff is null || login is null)
        {
            return handoff ?? login;
        }
        handoff["PATH"] = login["PATH"];
        return handoff;
    }

    /// <summary>Why the start failed, as it is shown: the last line code printed, or what the system said.</summary>
    private static string Describe(ProcessResult result)
    {
        var lines = (result.StandardError + "\n" + result.StandardOutput)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.LastOrDefault() ?? result.StartError ?? $"code exited with {result.ExitCode}";
    }
}
