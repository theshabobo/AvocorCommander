using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;

namespace AvocorCommander.ViewModels;

// ── Slot helper ───────────────────────────────────────────────────────────────

public sealed class PanelGridSlot : BaseViewModel
{
    public int Row { get; }
    public int Col { get; }

    private PanelButtonVM? _button;
    public PanelButtonVM? Button
    {
        get => _button;
        set
        {
            Set(ref _button, value);
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowAddPlaceholder));
        }
    }

    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            Set(ref _isEditMode, value);
            OnPropertyChanged(nameof(ShowAddPlaceholder));
            OnPropertyChanged(nameof(ShowEditOverlay));
        }
    }

    public bool IsEmpty           => Button == null;
    public bool ShowAddPlaceholder => IsEmpty && IsEditMode;
    public bool ShowEditOverlay    => !IsEmpty && IsEditMode;

    public PanelGridSlot(int row, int col) { Row = row; Col = col; }
}

// ── Button VM ─────────────────────────────────────────────────────────────────

public sealed class PanelButtonVM : BaseViewModel
{
    public PanelButton Model { get; }

    public List<PanelButtonAction> ActionsA { get; set; } = new();
    public List<PanelButtonAction> ActionsB { get; set; } = new();

    private bool _isToggleActive;
    public bool IsToggleActive
    {
        get => _isToggleActive;
        set => Set(ref _isToggleActive, value);
    }

    public string Label => Model.Label;
    public string Icon  => Model.Icon;
    public string Color => Model.Color;
    public bool   IsToggle => Model.IsToggle;

    public PanelButtonVM(PanelButton model) { Model = model; }

    public void Toggle() => IsToggleActive = !IsToggleActive;

    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(IsToggle));
    }
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public sealed class PanelViewModel : BaseViewModel
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connections;
    private readonly DispatcherTimer   _clock;

    // ── Observable state ─────────────────────────────────────────────────────

    public ObservableCollection<PanelScene>    Scenes        { get; } = new();
    public ObservableCollection<PanelPage>     Pages         { get; } = new();
    public ObservableCollection<PanelGridSlot> GridSlots     { get; } = new();
    public ObservableCollection<PanelButtonVM> BottomButtons { get; } = new();

    private PanelScene? _selectedScene;
    public PanelScene? SelectedScene
    {
        get => _selectedScene;
        set
        {
            if (Set(ref _selectedScene, value))
                OnSceneChanged();
        }
    }

    private PanelPage? _selectedPage;
    public PanelPage? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (Set(ref _selectedPage, value))
                RebuildGrid();
        }
    }

    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            Set(ref _isEditMode, value);
            foreach (var slot in GridSlots)
                slot.IsEditMode = value;
        }
    }

    private bool _isKioskMode;
    public bool IsKioskMode
    {
        get => _isKioskMode;
        set
        {
            Set(ref _isKioskMode, value);
            KioskModeChanged?.Invoke(this, value);
        }
    }

    private string _timeText = "";
    public string TimeText { get => _timeText; set => Set(ref _timeText, value); }

    private string _dateText = "";
    public string DateText { get => _dateText; set => Set(ref _dateText, value); }

    public bool HasScenes => Scenes.Count > 0;

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler<PanelGridSlot>? EditGridSlotRequested;
    public event EventHandler<PanelButtonVM?>? EditBottomButtonRequested;
    public event EventHandler?                AddSceneRequested;
    public event EventHandler<PanelScene>?    RenameSceneRequested;
    public event EventHandler?                AddPageRequested;
    public event EventHandler<PanelPage>?     RenamePageRequested;
    public event EventHandler<bool>?          KioskModeChanged;

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand AddSceneCommand           { get; }
    public ICommand RenameSceneCommand        { get; }
    public ICommand DeleteSceneCommand        { get; }
    public ICommand AddPageCommand            { get; }
    public ICommand RenamePageCommand         { get; }
    public ICommand DeletePageCommand         { get; }
    public ICommand EditGridSlotCommand       { get; }
    public ICommand RemoveGridButtonCommand   { get; }
    public ICommand ExecuteGridSlotCommand    { get; }
    public ICommand EditBottomButtonCommand   { get; }
    public ICommand AddBottomButtonCommand    { get; }
    public ICommand RemoveBottomButtonCommand { get; }
    public ICommand ExecuteBottomButtonCommand{ get; }
    public ICommand ToggleEditModeCommand     { get; }
    public ICommand ToggleKioskModeCommand    { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public PanelViewModel(DatabaseService db, ConnectionManager connections)
    {
        _db          = db;
        _connections = connections;

        AddSceneCommand     = new RelayCommand(() => AddSceneRequested?.Invoke(this, EventArgs.Empty));
        RenameSceneCommand  = new RelayCommand(() => { if (SelectedScene != null) RenameSceneRequested?.Invoke(this, SelectedScene); },
                                               () => SelectedScene != null);
        DeleteSceneCommand  = new RelayCommand(DeleteSelectedScene, () => SelectedScene != null);

        AddPageCommand      = new RelayCommand(() => AddPageRequested?.Invoke(this, EventArgs.Empty), () => SelectedScene != null);
        RenamePageCommand   = new RelayCommand(() => { if (SelectedPage != null) RenamePageRequested?.Invoke(this, SelectedPage); },
                                               () => SelectedPage != null);
        DeletePageCommand   = new RelayCommand(DeleteSelectedPage, () => SelectedPage != null);

        EditGridSlotCommand       = new RelayCommand<PanelGridSlot>(slot => { if (slot != null) EditGridSlotRequested?.Invoke(this, slot); });
        RemoveGridButtonCommand   = new RelayCommand<PanelGridSlot>(RemoveGridButton);
        ExecuteGridSlotCommand    = new AsyncRelayCommand<PanelGridSlot>(ExecuteGridSlotAsync);

        EditBottomButtonCommand    = new RelayCommand<PanelButtonVM?>(bvm => EditBottomButtonRequested?.Invoke(this, bvm));
        AddBottomButtonCommand     = new RelayCommand(() => EditBottomButtonRequested?.Invoke(this, null), () => SelectedScene != null);
        RemoveBottomButtonCommand  = new RelayCommand<PanelButtonVM>(RemoveBottomButton);
        ExecuteBottomButtonCommand = new AsyncRelayCommand<PanelButtonVM>(ExecuteButtonAsync);

        ToggleEditModeCommand  = new RelayCommand(() => IsEditMode = !IsEditMode);
        ToggleKioskModeCommand = new RelayCommand(() => IsKioskMode = !IsKioskMode);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();
        TickClock();
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    public void LoadData()
    {
        Scenes.Clear();
        foreach (var s in _db.GetAllPanelScenes())
            Scenes.Add(s);
        OnPropertyChanged(nameof(HasScenes));
        SelectedScene = Scenes.FirstOrDefault();
    }

    private void OnSceneChanged()
    {
        Pages.Clear();
        BottomButtons.Clear();
        GridSlots.Clear();

        if (SelectedScene == null) return;

        foreach (var p in _db.GetPagesForScene(SelectedScene.Id))
            Pages.Add(p);

        LoadBottomButtons();
        SelectedPage = Pages.FirstOrDefault();
    }

    private void LoadBottomButtons()
    {
        BottomButtons.Clear();
        if (SelectedScene == null) return;
        foreach (var b in _db.GetBottomButtonsForScene(SelectedScene.Id))
        {
            var bvm = new PanelButtonVM(b);
            var actions = _db.GetActionsForButton(b.Id);
            bvm.ActionsA = actions.Where(a => a.Phase == 0).ToList();
            bvm.ActionsB = actions.Where(a => a.Phase == 1).ToList();
            BottomButtons.Add(bvm);
        }
    }

    private void RebuildGrid()
    {
        GridSlots.Clear();
        // Always create 12 slots (3 rows × 4 cols)
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                GridSlots.Add(new PanelGridSlot(r, c) { IsEditMode = IsEditMode });

        if (SelectedPage == null) return;

        foreach (var btn in _db.GetGridButtonsForPage(SelectedPage.Id))
        {
            var slot = GridSlots.FirstOrDefault(s => s.Row == btn.GridRow && s.Col == btn.GridCol);
            if (slot == null) continue;
            var bvm = new PanelButtonVM(btn);
            var actions = _db.GetActionsForButton(btn.Id);
            bvm.ActionsA = actions.Where(a => a.Phase == 0).ToList();
            bvm.ActionsB = actions.Where(a => a.Phase == 1).ToList();
            slot.Button = bvm;
        }
    }

    // ── Scene / Page CRUD ─────────────────────────────────────────────────────

    public void AddScene(string name)
    {
        var s = new PanelScene { Name = name, SortOrder = Scenes.Count };
        s.Id = _db.InsertPanelScene(s);
        Scenes.Add(s);
        OnPropertyChanged(nameof(HasScenes));
        SelectedScene = s;
    }

    public void RenameScene(PanelScene scene, string newName)
    {
        scene.Name = newName;
        _db.UpdatePanelScene(scene);
        // force UI refresh — PanelScene is not an ObservableObject
        var idx = Scenes.IndexOf(scene);
        if (idx >= 0) { Scenes.RemoveAt(idx); Scenes.Insert(idx, scene); }
        SelectedScene = scene;
    }

    private void DeleteSelectedScene()
    {
        if (SelectedScene == null) return;
        _db.DeletePanelScene(SelectedScene.Id);
        var toRemove = SelectedScene;
        SelectedScene = Scenes.FirstOrDefault(s => s != toRemove);
        Scenes.Remove(toRemove);
        OnPropertyChanged(nameof(HasScenes));
    }

    public void AddPage(string name)
    {
        if (SelectedScene == null) return;
        var p = new PanelPage { SceneId = SelectedScene.Id, Name = name, SortOrder = Pages.Count };
        p.Id = _db.InsertPanelPage(p);
        Pages.Add(p);
        SelectedPage = p;
    }

    public void RenamePage(PanelPage page, string newName)
    {
        page.Name = newName;
        _db.UpdatePanelPage(page);
        var idx = Pages.IndexOf(page);
        if (idx >= 0) { Pages.RemoveAt(idx); Pages.Insert(idx, page); }
        SelectedPage = page;
    }

    private void DeleteSelectedPage()
    {
        if (SelectedPage == null) return;
        _db.DeletePanelPage(SelectedPage.Id);
        var toRemove = SelectedPage;
        SelectedPage = Pages.FirstOrDefault(p => p != toRemove);
        Pages.Remove(toRemove);
    }

    // ── Grid button CRUD ─────────────────────────────────────────────────────

    /// <summary>Called by dialog result after user configures a grid slot.</summary>
    public void SaveGridSlot(PanelGridSlot slot, PanelButton buttonData, List<PanelButtonAction> actionsA, List<PanelButtonAction> actionsB)
    {
        if (SelectedPage == null) return;

        if (slot.Button == null)
        {
            // New button
            buttonData.PageId     = SelectedPage.Id;
            buttonData.SceneId    = SelectedScene?.Id;
            buttonData.ButtonType = "grid";
            buttonData.GridRow    = slot.Row;
            buttonData.GridCol    = slot.Col;
            buttonData.Id = _db.InsertPanelButton(buttonData);
        }
        else
        {
            // Update existing
            buttonData.Id = slot.Button.Model.Id;
            _db.UpdatePanelButton(buttonData);
        }

        var allActions = actionsA.Concat(actionsB).ToList();
        _db.SetActionsForButton(buttonData.Id, allActions);

        // Reload slot
        var bvm = new PanelButtonVM(buttonData);
        bvm.ActionsA = actionsA;
        bvm.ActionsB = actionsB;
        bvm.IsToggleActive = slot.Button?.IsToggleActive ?? false;
        slot.Button = bvm;
    }

    private void RemoveGridButton(PanelGridSlot? slot)
    {
        if (slot?.Button == null) return;
        _db.DeletePanelButton(slot.Button.Model.Id);
        slot.Button = null;
    }

    // ── Bottom button CRUD ────────────────────────────────────────────────────

    public void SaveBottomButton(PanelButtonVM? existing, PanelButton buttonData, List<PanelButtonAction> actionsA, List<PanelButtonAction> actionsB)
    {
        if (SelectedScene == null) return;

        if (existing == null)
        {
            buttonData.SceneId    = SelectedScene.Id;
            buttonData.ButtonType = "bottom";
            buttonData.SortOrder  = BottomButtons.Count;
            buttonData.Id = _db.InsertPanelButton(buttonData);
        }
        else
        {
            buttonData.Id = existing.Model.Id;
            _db.UpdatePanelButton(buttonData);
        }

        var allActions = actionsA.Concat(actionsB).ToList();
        _db.SetActionsForButton(buttonData.Id, allActions);

        LoadBottomButtons();
    }

    private void RemoveBottomButton(PanelButtonVM? bvm)
    {
        if (bvm == null) return;
        _db.DeletePanelButton(bvm.Model.Id);
        BottomButtons.Remove(bvm);
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private async Task ExecuteGridSlotAsync(PanelGridSlot? slot)
    {
        if (slot?.Button == null) return;
        await ExecuteButtonAsync(slot.Button);
    }

    private async Task ExecuteButtonAsync(PanelButtonVM? bvm)
    {
        if (bvm == null) return;

        var actions = bvm.IsToggle && bvm.IsToggleActive
            ? bvm.ActionsB
            : bvm.ActionsA;

        foreach (var action in actions)
        {
            if (!_connections.IsConnected(action.DeviceId)) continue;

            byte[] bytes;
            if (action.CommandFormat == "ASCII")
                bytes = System.Text.Encoding.ASCII.GetBytes(action.CommandCode + "\r");
            else
            {
                try
                {
                    bytes = action.CommandCode
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => System.Convert.ToByte(t, 16))
                        .ToArray();
                }
                catch { continue; }
            }

            await _connections.SendAsync(action.DeviceId, bytes);
            await Task.Delay(500);
        }

        if (bvm.IsToggle)
            bvm.Toggle();
    }

    // ── Clock ─────────────────────────────────────────────────────────────────

    private void TickClock()
    {
        var now = DateTime.Now;
        TimeText = now.ToString("h:mm tt");
        DateText = now.ToString("ddd, MMM d");
    }

    public void Dispose() => _clock.Stop();
}
