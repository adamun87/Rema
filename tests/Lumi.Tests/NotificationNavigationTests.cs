using System.Threading;
using Avalonia.Headless;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class NotificationNavigationArgumentTests
{
    [Fact]
    public void ActivationArguments_RoundTripChatId()
    {
        var chatId = Guid.NewGuid();

        var activationArguments = NotificationService.CreateActivationArguments(chatId);
        var parsedChatId = NotificationService.ParseChatIdFromActivationArguments(activationArguments);

        Assert.Equal(chatId, parsedChatId);
    }

    [Fact]
    public void ParseChatIdFromActivationArguments_IgnoresInvalidPayload()
    {
        Assert.Null(NotificationService.ParseChatIdFromActivationArguments(null));
        Assert.Null(NotificationService.ParseChatIdFromActivationArguments(string.Empty));
        Assert.Null(NotificationService.ParseChatIdFromActivationArguments("kind=main"));
        Assert.Null(NotificationService.ParseChatIdFromActivationArguments("chatId=not-a-guid"));
    }
}

[Collection("Headless UI")]
public sealed class NotificationNavigationTests
{
    [Fact]
    public async Task OpenChatByIdAsync_LoadsRequestedChatAndShowsChatTab()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var firstChat = new Chat
            {
                Title = "First",
                UpdatedAt = DateTimeOffset.Now.AddMinutes(-10)
            };
            firstChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "First reply" });

            var secondChat = new Chat
            {
                Title = "Second",
                UpdatedAt = DateTimeOffset.Now
            };
            secondChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Second reply" });

            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(firstChat);
            data.Chats.Add(secondChat);

            var vm = new MainViewModel(new DataStore(data), new CopilotService(), new UpdateService())
            {
                SelectedNavIndex = 4
            };

            var opened = await vm.OpenChatByIdAsync(firstChat.Id);

            Assert.True(opened);
            Assert.Equal(firstChat.Id, vm.ChatVM.CurrentChat?.Id);
            Assert.Equal(0, vm.SelectedNavIndex);
        }, CancellationToken.None);
    }
}
