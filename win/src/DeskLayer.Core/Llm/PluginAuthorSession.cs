// Runs the conversation that writes a plugin — the Windows twin of the mac
// PluginAuthorSession: system prompt built from the bundled API docs, then a
// tool-calling loop until the model stops asking for tools or the turn limit
// is reached.
//
// Nothing reaches the plugins folder until the run finishes and the result
// validates. That keeps a half-written plugin off the desktop, and avoids
// waking the folder watcher on every write — which would rebuild every
// running item, repeatedly.
//
// UI-framework-free: state changes raise Changed on whatever thread the run
// is on; the WPF dialog marshals to its dispatcher.

using System.IO;
using DeskLayer.Core.Model;

namespace DeskLayer.Core.Llm;

public sealed class PluginAuthorSession
{
    /// One line in the dialog's progress list.
    public sealed class Step
    {
        public string Text = "";
        public string? Detail;
        public bool IsError;
    }

    /// What a run is working on. Editing feeds the existing source to the
    /// model; replacing installs over the original, copying leaves it alone.
    public enum SubjectKind { NewPlugin, Replace, Copy }
    public readonly record struct Subject(SubjectKind Kind, string? BasePluginId)
    {
        public static Subject New => new(SubjectKind.NewPlugin, null);
        public static Subject Replace(string id) => new(SubjectKind.Replace, id);
        public static Subject Copy(string id) => new(SubjectKind.Copy, id);
    }

    public List<Step> Steps { get; } = new();
    public bool IsRunning { get; private set; }
    /// True while the model list is being fetched, so the button can say so.
    public bool IsFetchingModels { get; private set; }
    /// Set when a run finishes with a plugin installed.
    public string? InstalledPluginId { get; private set; }
    public string? Error { get; set; }

    public LlmSettings Settings { get; }

    /// Raised after any observable state change (steps, flags, error).
    public event Action? Changed;

    private readonly PluginRegistry registry;
    private readonly PluginStoreRegistry stores;
    private readonly ChatClient client = new();
    private readonly Action<string> log;
    private CancellationTokenSource? cancelSource;

    public PluginAuthorSession(PluginRegistry registry, PluginStoreRegistry stores, Action<string> log)
    {
        this.registry = registry;
        this.stores = stores;
        this.log = log;
        Settings = LlmSettings.Load();
    }

    public string ApiKey
    {
        get => LlmSettings.ApiKey ?? "";
        set => LlmSettings.ApiKey = string.IsNullOrEmpty(value) ? null : value;
    }

    /// A plugin that came from a store must not be rewritten in place: the
    /// store's next update overwrites the file, and the user's changes go
    /// with it. Rewrites of those are installed as copies instead.
    public bool IsStoreInstalled(string pluginId) => stores.OriginOf(pluginId) != null;

    /// Applies that rule. Called on the way in, so the whole run — the
    /// prompt, the install name — agrees on what is being written.
    public Subject Resolved(Subject subject)
    {
        if (subject.Kind == SubjectKind.Replace && subject.BasePluginId is { } base_ && IsStoreInstalled(base_))
            return Subject.Copy(base_);
        return subject;
    }

    /// Fills the model picker from `{baseURL}/models`. Only ever called from
    /// the button: the list is cached until the user asks again.
    public async void FetchModels()
    {
        if (IsFetchingModels) return;
        IsFetchingModels = true;
        Error = null;
        Notify();
        var (models, error) = await client.ListModels(Settings, LlmSettings.ApiKey ?? "");
        IsFetchingModels = false;
        if (models != null)
        {
            Settings.CachedModels = models;
            // A model that is gone from the endpoint would fail at the first
            // request; move to one that exists.
            if (!models.Contains(Settings.Model) && models.Count > 0)
                Settings.Model = models[0];
            Settings.Save();
        }
        else
        {
            Error = error;
        }
        Notify();
    }

    /// Clears the last run's outcome — the "Show X" button shouldn't point
    /// at a previous result once the user picks a different base.
    public void ClearResult()
    {
        if (IsRunning) return;
        InstalledPluginId = null;
        Error = null;
        Steps.Clear();
        Notify();
    }

    public void Cancel()
    {
        cancelSource?.Cancel();
        cancelSource = null;
        IsRunning = false;
        Add(L.T("Stopped."));
    }

    /// Asks the model for a plugin and installs what it produces.
    public async void Start(string prompt, Subject subject)
    {
        if (IsRunning) return;
        Settings.Save();
        if (!Settings.IsConfigured)
        {
            Error = L.T("Set the base URL and model first.");
            Notify();
            return;
        }
        if (!PluginDocs.IsAvailable)
        {
            Error = L.T("This build is missing the plugin API documentation.");
            Notify();
            return;
        }
        Steps.Clear();
        Error = null;
        InstalledPluginId = null;
        IsRunning = true;

        subject = Resolved(subject);
        if (subject.Kind == SubjectKind.Copy && subject.BasePluginId is { } base_ && IsStoreInstalled(base_))
            Add(L.T("{0} came from a store, so this is saved as a copy.", base_));

        var tools = new PluginTools(registry);
        cancelSource = new CancellationTokenSource();
        try
        {
            await Run(prompt, subject, tools, cancelSource.Token);
        }
        finally
        {
            tools.CleanUp();
            IsRunning = false;
            Notify();
        }
    }

    private async Task Run(string prompt, Subject subject, PluginTools tools, CancellationToken cancel)
    {
        var key = LlmSettings.ApiKey ?? "";
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt()),
            ChatMessage.User(Request(prompt, subject)),
        };

        Add(L.T("Asking {0}…", Settings.Model));

        var maxTurns = Math.Max(Settings.MaxTurns, 1);
        for (var turn = 1; turn <= maxTurns; turn++)
        {
            if (cancel.IsCancellationRequested) return;

            var result = await client.Send(messages, PluginTools.Specs, Settings, key, cancel);
            if (cancel.IsCancellationRequested) return;

            if (result.Error is { } failure)
            {
                Error = failure;
                Add(L.T("Failed"), failure, isError: true);
                return;
            }
            if (result.Text is { } text)
            {
                // No more tools wanted: the model is done talking.
                Finish(text, subject, tools);
                return;
            }
            if (result.Tools is { } asked)
            {
                messages.Add(asked.Assistant);
                foreach (var call in asked.Calls)
                {
                    if (cancel.IsCancellationRequested) return;
                    Add(Describe(call));
                    var output = tools.Run(call);
                    messages.Add(ChatMessage.ToolResult(output, call.Id));
                    if (output.StartsWith("error:", StringComparison.Ordinal))
                    {
                        // Visible in the log, and the model gets it too.
                        Steps[^1].Detail = output;
                        Steps[^1].IsError = true;
                        Notify();
                    }
                }
                if (turn == maxTurns)
                {
                    Add(L.T("Reached the turn limit."), isError: true);
                    Finish("", subject, tools);
                    return;
                }
            }
        }
    }

    /// Installs whatever validated, or explains why nothing did.
    private void Finish(string text, Subject subject, PluginTools tools)
    {
        if (tools.Written.Count == 0)
        {
            Error = text.Length == 0 ? L.T("The model didn't write a plugin.") : text;
            Add(L.T("No plugin was written."), text.Length == 0 ? null : text, isError: true);
            return;
        }
        var written = tools.Written[0];
        // Where it lands is the app's decision, not the model's: replacing
        // must hit the original even if the model renamed it, and copying
        // must never overwrite the plugin it was based on.
        var name = InstallName(written, subject);
        var staged = tools.StagedPath(written);
        string source;
        try
        {
            source = File.ReadAllText(staged ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = L.T("Couldn't read the generated plugin.");
            Notify();
            return;
        }
        var (ok, message) = PluginMetadata.Validate(source);
        if (!ok)
        {
            Error = message;
            Add(L.T("The generated plugin isn't valid."), message, isError: true);
            return;
        }

        var destination = Path.Combine(PluginRegistry.PluginsDirectory, $"{name}.js");
        // Never overwrite a working plugin without a copy to go back to.
        if (File.Exists(destination))
        {
            try
            {
                File.Copy(destination, destination + ".bak", overwrite: true);
                Add(L.T("Kept the previous version as {0}.js.bak", name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            Directory.CreateDirectory(PluginRegistry.PluginsDirectory);
            File.WriteAllText(destination, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            Notify();
            return;
        }
        registry.Rescan();
        InstalledPluginId = name;

        var (version, _) = PluginMetadata.Extract(source);
        var detail = message;
        if (version != null) detail += $" v{version}";
        Add(L.T("Installed {0}", name), detail);
        log($"authored plugin {name}");
    }

    /// The name to install under, given what the model called its file.
    public string InstallName(string written, Subject subject)
    {
        switch (subject.Kind)
        {
            case SubjectKind.NewPlugin:
                return written;
            case SubjectKind.Replace:
                return subject.BasePluginId ?? written;
            case SubjectKind.Copy:
                var base_ = subject.BasePluginId;
                if (base_ == null || written != base_) return written;
                // The model reused the base's name for what should be a
                // copy; step it aside rather than clobbering the original.
                var n = 2;
                var candidate = $"{base_} 2";
                while (registry.Plugin(candidate) != null)
                {
                    n++;
                    candidate = $"{base_} {n}";
                }
                return candidate;
            default:
                return written;
        }
    }

    /// The user's request, with the existing source when editing — pasting
    /// it in is more reliable than hoping the model calls read_file first.
    private string Request(string prompt, Subject subject)
    {
        var base_ = subject.BasePluginId;
        var descriptor = base_ == null ? null : registry.Plugin(base_);
        string? source = null;
        if (descriptor != null)
        {
            try { source = File.ReadAllText(descriptor.SourcePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (base_ == null || source == null) return prompt;

        var naming = subject.Kind switch
        {
            SubjectKind.Replace => $"Write the result with write_plugin using the same name, \"{base_}\".",
            SubjectKind.Copy => $"This is a variation: write it with write_plugin under a NEW name, not \"{base_}\".",
            _ => "",
        };
        return $"""
            Change this existing plugin. Keep what works and change only what the request asks for.

            Request: {prompt}

            {naming}

            Current source of "{base_}":

            ```js
            {source}
            ```
            """;
    }

    // MARK: - Prompt

    private string SystemPrompt() => $"""
        You write plugins for DeskLayer, an app that renders JavaScript onto the desktop wallpaper, into floating windows, or as widgets. This is the Windows build.

        Work like this:
        1. Write the plugin with write_plugin.
        2. Call validate_plugin and fix anything it reports.
        3. Reply with one short sentence describing what you made.

        Rules that matter:
        - The runtime is a plain JS engine. There is no DOM, no window, no document, no require, no Node API. Only the APIs in the declarations exist.
        - render() must RETURN its view tree: `render = () => view([...])`. A block body needs an explicit `return`.
        - render(ctx) with an argument means canvas mode and draws instead.
        - Declare `permissions` only for host APIs you actually call (shell, ssh, server). Prefer none; applescript is unavailable on Windows.
        - Give plugin.export a version, author, description, and a sensible width/height in points.
        - Read plugin.d.ts or plugin-guide.md when unsure. Do not invent APIs.

        Here are the TypeScript declarations for everything available:

        {PluginDocs.Declarations}

        A complete working plugin, for shape:

        ```js
        {ExampleSource()}
        ```
        """;

    /// A real installed plugin reads better as an example than an invented
    /// one, and proves the shape actually runs in this app.
    private string ExampleSource()
    {
        foreach (var id in new[] { "HelloCard", "AnalogClock" })
        {
            var descriptor = registry.Plugin(id);
            if (descriptor == null) continue;
            try { return File.ReadAllText(descriptor.SourcePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return PluginDocs.Example();
    }

    private static string Describe(ToolCall call)
    {
        var name = call.StringArgument("name");
        return call.Name switch
        {
            "list_plugins" => L.T("Listing installed plugins…"),
            "read_file" => L.T("Reading {0}…", name ?? "a file"),
            "write_plugin" => L.T("Writing {0}…", name ?? "the plugin"),
            "validate_plugin" => L.T("Validating {0}…", name ?? "the plugin"),
            _ => call.Name,
        };
    }

    private void Add(string text, string? detail = null, bool isError = false)
    {
        Steps.Add(new Step { Text = text, Detail = detail, IsError = isError });
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}
