using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rema.Models;
using Rema.Services;

namespace Rema.ViewModels;

public partial class CapabilitiesViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    [ObservableProperty] private CapabilityDefinition? _selectedCapability;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editContent = "";
    [ObservableProperty] private string _editTags = "";
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _hasCapabilities;
    [ObservableProperty] private bool _canDeleteSelectedCapability;
    [ObservableProperty] private bool _hasSelectedCapability;
    [ObservableProperty] private string _selectedSourceText = "";

    public string Kind { get; }
    public string Title { get; }
    public string EmptyText { get; }
    public ObservableCollection<CapabilityDefinition> Capabilities { get; } = [];

    public CapabilitiesViewModel(DataStore dataStore, string kind)
    {
        _dataStore = dataStore;
        Kind = kind;
        Title = kind switch
        {
            "Skill" => "Skills",
            "Mcp" => "MCP Servers",
            "Tool" => "Tools",
            "Agent" => "Agents",
            _ => kind
        };
        EmptyText = $"No {Title.ToLowerInvariant()} configured yet.";
        Refresh();
    }

    public void Refresh()
    {
        Capabilities.Clear();
        var query = _dataStore.Data.Capabilities
            .Where(c => c.Kind.Equals(Kind, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(c =>
                c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                || c.Content.Contains(search, StringComparison.OrdinalIgnoreCase)
                || c.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var capability in query.OrderByDescending(c => c.IsBuiltIn).ThenBy(c => c.Name))
            Capabilities.Add(capability);

        HasCapabilities = Capabilities.Count > 0;
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedCapabilityChanged(CapabilityDefinition? value)
    {
        HasSelectedCapability = value is not null;
        SelectedSourceText = value?.Source ?? "";
        if (value is null) return;
        EditName = value.Name;
        EditDescription = value.Description;
        EditContent = value.Content;
        EditTags = string.Join(", ", value.Tags);
        EditIsEnabled = value.IsEnabled;
        CanDeleteSelectedCapability = !value.IsBuiltIn;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditCapability(CapabilityDefinition capability)
    {
        SelectedCapability = capability;
    }

    [RelayCommand]
    private void NewCapability()
    {
        SelectedCapability = null;
        EditName = "";
        EditDescription = "";
        EditContent = "";
        EditTags = "";
        EditIsEnabled = true;
        CanDeleteSelectedCapability = false;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task SaveCapability()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        var capability = SelectedCapability ?? new CapabilityDefinition { Kind = Kind, Source = "manual" };
        if (SelectedCapability is null)
            _dataStore.Data.Capabilities.Add(capability);

        capability.Kind = Kind;
        capability.Name = EditName.Trim();
        capability.Description = EditDescription.Trim();
        capability.Content = EditContent.Trim();
        capability.IsEnabled = EditIsEnabled;
        capability.Tags = EditTags
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _dataStore.SaveAsync();
        SelectedCapability = capability;
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteCapability()
    {
        if (SelectedCapability is null || SelectedCapability.IsBuiltIn) return;

        _dataStore.Data.Capabilities.Remove(SelectedCapability);
        SelectedCapability = null;
        HasSelectedCapability = false;
        SelectedSourceText = "";
        CanDeleteSelectedCapability = false;
        IsEditing = false;
        await _dataStore.SaveAsync();
        Refresh();
    }

    [RelayCommand]
    private async Task ToggleCapability()
    {
        if (SelectedCapability is null) return;

        SelectedCapability.IsEnabled = !SelectedCapability.IsEnabled;
        EditIsEnabled = SelectedCapability.IsEnabled;
        await _dataStore.SaveAsync();
        Refresh();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        SelectedCapability = null;
        IsEditing = false;
    }
}
