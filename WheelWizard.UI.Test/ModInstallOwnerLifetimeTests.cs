using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NSubstitute;
using WheelWizard.GameBanana;
using WheelWizard.GameBanana.Domain;
using WheelWizard.Mods;
using WheelWizard.Mods.Views.Dialogs;
using WheelWizard.Shared;
using WheelWizard.Shared.Downloads;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Dialogs.Base;
using WheelWizard.Views.Shell;

namespace WheelWizard.UI.Test;

public class ModInstallOwnerLifetimeTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ClosedSurface_CancelsDownloadAndSuppressesLateSuccessOrFailureDialogs(bool downloadFails, bool closeShell)
    {
        var manager = Substitute.For<IModManager>();
        var gameBanana = Substitute.For<IGameBananaSingletonService>();
        var downloads = Substitute.For<IDownloadService>();
        var paths = Substitute.For<IModPaths>();
        var presentation = Substitute.For<IModOperationPresentation>();
        var directory = Path.Combine(Path.GetTempPath(), "wheelwizard-owner-test-" + Guid.NewGuid());
        paths.DownloadFolderPath.Returns(directory);
        var started = new TaskCompletionSource<CancellationToken>();
        var finished = new TaskCompletionSource<OperationResult<string>>();
        downloads
            .DownloadAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<IProgress<DownloadProgress>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                started.SetResult(call.Arg<CancellationToken>());
                // Deliberately return late instead of honoring cancellation, to exercise the race.
                return finished.Task;
            });
        var content = new ModContent(manager, gameBanana, downloads, Substitute.For<IGameBananaMediaService>(), paths, presentation);
        typeof(ModContent).GetProperty("CurrentMod", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(content, CreateMod());
        var owner = new TestWindow();
        owner.Show();
        var surface = new ModSurface { Content = content };
        surface.SetModalOwner(owner);
        var surfaceClosed = surface.ShowDialog();
        owner.UpdateLayout();
        // Keep another app window open: late failures must not move to a different owner.
        var other = new TestWindow();
        other.Show();
        try
        {
            var install = (Task)
                typeof(ModContent)
                    .GetMethod("InstallCurrentModAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(content, null)!;
            var question = Assert.IsType<YesNoWindow>(owner.Modals.TopModal);
            question.FindControl<ActionButton>("YesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (closeShell)
                owner.Close();
            else
                surface.Close();
            await surfaceClosed.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(token.IsCancellationRequested);
            Assert.Equal(!closeShell, owner.IsVisible);
            Assert.Null(TopLevel.GetTopLevel(content));
            File.WriteAllText(Path.Combine(directory, "mod.zip"), "Downloaded test file");
            finished.SetResult(
                downloadFails
                    ? (OperationResult<string>)new OperationError { Message = "Late failure" }
                    : OperationResult.Ok(Path.Combine(directory, "mod.zip"))
            );
            await install.WaitAsync(TimeSpan.FromSeconds(2));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(owner.Modals.HasModals);
            Assert.False(other.Modals.HasModals);
            Assert.Empty(presentation.ReceivedCalls());
            Assert.Empty(manager.ReceivedCalls());
            Assert.Empty(gameBanana.ReceivedCalls());
        }
        finally
        {
            owner.Close();
            other.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private sealed class ModSurface : PopupContent { }

    private sealed class TestWindow : BaseWindow
    {
        private readonly Border _content = new();
        private readonly Border _overlay = new();
        protected override Control InteractionContent => _content;
        protected override Control InteractionOverlay => _overlay;

        public TestWindow()
        {
            Width = 1280;
            Height = 720;
            Content = _content;
        }
    }

    private static GameBananaModDetails CreateMod() =>
        new()
        {
            Id = 123,
            Name = "Test mod",
            Version = "1",
            ProfileUrl = "https://example.com/mod",
            LikeCount = 0,
            ViewCount = 0,
            DateAdded = 0,
            DateModified = 0,
            IsObsolete = false,
            Author = new GameBananaAuthor { Name = "Author", ProfileUrl = "https://example.com/author" },
            Game = new GameBananaGame
            {
                Name = "Mario Kart Wii",
                ProfileUrl = "https://example.com/game",
                IconUrl = "",
            },
            Category = new GameBananaCategory { Name = "Maps", ProfileUrl = "https://example.com/maps" },
            Text = "Test mod",
            License = "",
            DownloadCount = 0,
            Files = new List<GameBananaModFiles>
            {
                new() { FileName = "mod.zip", DownloadUrl = "https://example.com/mod.zip" },
            },
        };
}
