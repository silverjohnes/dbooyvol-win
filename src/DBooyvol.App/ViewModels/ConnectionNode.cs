using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using DBooyvol.Core.Db;
using DBooyvol.Core.Models;

namespace DBooyvol.App.ViewModels;

public sealed class ConnectionNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isConnected;
    private bool _isSelected;
    private bool _loaded;
    private bool _loading;
    private Session? _session;

    public ConnectionNode(MainViewModel owner, Profile? profile, string label, string kind,
        string id, bool hasChildren, bool primary, IReadOnlyList<string>? path = null,
        ConnectionNode? parent = null)
    {
        Owner = owner;
        Profile = profile;
        Label = label;
        NodeKind = kind;
        Id = id;
        HasChildren = hasChildren;
        IsPrimary = primary;
        Path = path?.ToArray() ?? Array.Empty<string>();
        Parent = parent;
        if (hasChildren) Children.Add(Placeholder());
    }

    public MainViewModel Owner { get; }
    public Profile? Profile { get; }
    public string Label { get; }
    public string NodeKind { get; }
    public string Id { get; }
    public bool HasChildren { get; }
    public bool IsPrimary { get; }
    public IReadOnlyList<string> Path { get; }
    public ConnectionNode? Parent { get; }
    public ObservableCollection<ConnectionNode> Children { get; } = new();

    public ConnectionNode Root => Parent?.Root ?? this;

    public Session? Session
    {
        get => _session;
        private set
        {
            if (!SetProperty(ref _session, value)) return;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) _ = LoadChildrenAsync();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsPlaceholder => NodeKind == "placeholder";
    public bool IsConnection => NodeKind == "connection";
    public bool IsObject => NodeKind is "table" or "view";
    public bool IsFolder => NodeKind is "schema" or "folder";

    public string Icon => NodeKind switch
    {
        "connection" => IsConnected ? "●" : "○",
        "schema" => "▦",
        "folder" => "▸",
        "table" => "▤",
        "view" => "◫",
        "column" => "·",
        _ => ""
    };

    private static ConnectionNode Placeholder() =>
        new(null!, null, "…", "placeholder", "", false, false);

    public async Task<bool> EnsureConnectedAsync()
    {
        if (Profile is null) return false;
        if (Session is not null) return true;
        try
        {
            var session = await Owner.CreateSessionAsync(Profile).ConfigureAwait(true);
            Session = session;
            IsConnected = true;
            return true;
        }
        catch (Exception error)
        {
            Owner.ReportError("Не удалось подключиться", error);
            return false;
        }
    }

    public async Task LoadChildrenAsync(bool force = false)
    {
        if (HasChildren == false && !IsConnection) return;
        if (_loading) return;
        if (_loaded && !force) return;
        if (Path.Count == 0 && !await EnsureConnectedAsync().ConfigureAwait(true)) return;

        var root = Root;
        if (root.Session is null || root.Profile is null) return;

        _loading = true;
        try
        {
            if (force) Children.Clear();
            var response = await Owner.Sidecar.CallAsync("tree", new
            {
                connId = root.Session.ConnId,
                kind = root.Profile.Kind,
                database = root.Profile.Database,
                path = Path
            }).ConfigureAwait(true);

            Children.Clear();
            var nodes = response?["nodes"] as JsonArray;
            if (nodes is not null)
            {
                foreach (var node in nodes.OfType<JsonObject>())
                {
                    var id = node["id"]?.GetValue<string>() ?? "";
                    var label = node["label"]?.GetValue<string>() ?? id;
                    var kind = node["kind"]?.GetValue<string>() ?? "";
                    var hasChildren = node["hasChildren"]?.GetValue<bool>() ?? false;
                    var primary = node["primary"]?.GetValue<bool>() ?? false;
                    var childPath = ChildPath(id, label);
                    Children.Add(new ConnectionNode(Owner, null, label, kind, id,
                        hasChildren, primary, childPath, this));
                }
            }
            _loaded = true;
        }
        catch (Exception error)
        {
            Owner.ReportError("Не удалось прочитать дерево", error);
        }
        finally
        {
            _loading = false;
        }
    }

    private string[] ChildPath(string id, string label)
    {
        return Path.Count switch
        {
            0 => new[] { id },
            1 => new[] { Path[0], id },
            2 => new[] { Path[0], Path[1], label },
            _ => Path.Concat(new[] { label }).ToArray()
        };
    }

    public async Task DisconnectAsync()
    {
        if (Session is not null)
        {
            await Session.DisposeAsync().ConfigureAwait(true);
            Session = null;
        }
        IsConnected = false;
        _loaded = false;
        Children.Clear();
        if (HasChildren) Children.Add(Placeholder());
    }

    public async Task RefreshAsync()
    {
        _loaded = false;
        await LoadChildrenAsync(force: true).ConfigureAwait(true);
    }

    public string QualifiedName()
    {
        if (Path.Count >= 3) return $"{Path[0]}.{Path[2]}";
        return Label;
    }
}
