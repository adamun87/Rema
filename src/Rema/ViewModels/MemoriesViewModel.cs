using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rema.Models;
using Rema.Services;

namespace Rema.ViewModels;

public partial class MemoriesViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    [ObservableProperty] private Memory? _selectedMemory;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editKey = "";
    [ObservableProperty] private string _editContent = "";
    [ObservableProperty] private string _editCategory = "General";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _hasMemories;

    public ObservableCollection<Memory> Memories { get; } = [];

    public MemoriesViewModel(DataStore dataStore)
    {
        _dataStore = dataStore;
        Refresh();
    }

    public void Refresh()
    {
        Memories.Clear();
        var query = _dataStore.Data.Memories.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(m =>
                m.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                || m.Content.Contains(search, StringComparison.OrdinalIgnoreCase)
                || m.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var memory in query.OrderBy(m => m.Category).ThenBy(m => m.Key))
            Memories.Add(memory);

        HasMemories = Memories.Count > 0;
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedMemoryChanged(Memory? value)
    {
        if (value is null) return;
        EditKey = value.Key;
        EditContent = value.Content;
        EditCategory = value.Category;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditMemory(Memory memory)
    {
        SelectedMemory = memory;
    }

    [RelayCommand]
    private void NewMemory()
    {
        SelectedMemory = null;
        EditKey = "";
        EditContent = "";
        EditCategory = "General";
        IsEditing = true;
    }

    [RelayCommand]
    private async Task SaveMemory()
    {
        if (string.IsNullOrWhiteSpace(EditKey) || string.IsNullOrWhiteSpace(EditContent))
            return;

        var memory = SelectedMemory ?? new Memory { Source = "manual" };
        if (SelectedMemory is null)
            _dataStore.Data.Memories.Add(memory);

        memory.Key = EditKey.Trim();
        memory.Content = EditContent.Trim();
        memory.Category = string.IsNullOrWhiteSpace(EditCategory) ? "General" : EditCategory.Trim();
        memory.UpdatedAt = DateTimeOffset.Now;

        await _dataStore.SaveAsync();
        SelectedMemory = memory;
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteMemory()
    {
        if (SelectedMemory is null) return;

        _dataStore.Data.Memories.Remove(SelectedMemory);
        SelectedMemory = null;
        IsEditing = false;
        await _dataStore.SaveAsync();
        Refresh();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        SelectedMemory = null;
        IsEditing = false;
    }
}
