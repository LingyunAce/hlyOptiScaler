using Deployer.Core.Domain;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Deployer.App.Models;

public sealed class GameListItem(DiscoveredGame game) : INotifyPropertyChanged
{
    private string? _selectedExecutable = game.ExecutableCandidates.Count == 1 ? game.ExecutableCandidates[0] : null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public DiscoveredGame Game { get; } = game;
    public string Title => Game.Title;
    public GamePlatform Platform => Game.Platform;
    public string InstallRoot => Game.InstallRoot;
    public DirectoryWritability Writability => Game.Writability;
    public IReadOnlyList<string> ExecutableCandidates => Game.ExecutableCandidates;
    public string? SelectedExecutable { get => _selectedExecutable; set { if (_selectedExecutable == value) return; _selectedExecutable = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedExecutable))); } }
    public override string ToString() => $"{Title} ({Platform})";
}
