using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobsViewModelTests
{
    [Fact]
    public void RefreshFromStore_SelectsFirstJob_WhenJobsExistAndNothingIsSelected()
    {
        var chat = CreateChat("Daily planning");
        var job = CreateJob(chat.Id, "Daily plan");
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);

        Assert.Same(job, harness.ViewModel.SelectedJob);
        Assert.True(harness.ViewModel.IsEditing);
        Assert.Equal("Daily plan", harness.ViewModel.EditName);
    }

    [Fact]
    public void RefreshFromStore_SelectsPreferredChatJob_WhenOpeningJobsFromChat()
    {
        var otherChat = CreateChat("Other chat");
        var preferredChat = CreateChat("Hotel search");
        var otherJob = CreateJob(otherChat.Id, "A unrelated job");
        var preferredJob = CreateJob(preferredChat.Id, "Z hotel monitor");
        var data = new AppData
        {
            Chats = [otherChat, preferredChat],
            BackgroundJobs = [otherJob, preferredJob]
        };
        using var harness = CreateHarness(data);

        Assert.Same(otherJob, harness.ViewModel.SelectedJob);

        harness.ViewModel.SetPreferredChat(preferredChat);
        harness.ViewModel.RefreshFromStore();

        Assert.Same(preferredJob, harness.ViewModel.SelectedJob);
        Assert.Equal("Z hotel monitor", harness.ViewModel.EditName);
    }

    [Fact]
    public void DeleteSelectedJob_SelectsRemainingJob()
    {
        var chat = CreateChat("Monitoring");
        var firstJob = CreateJob(chat.Id, "A first");
        var secondJob = CreateJob(chat.Id, "B second");
        var data = new AppData { Chats = [chat], BackgroundJobs = [firstJob, secondJob] };
        using var harness = CreateHarness(data);

        harness.ViewModel.DeleteJobCommand.Execute(firstJob);

        Assert.Same(secondJob, harness.ViewModel.SelectedJob);
        Assert.True(harness.ViewModel.IsEditing);
    }

    private static TestHarness CreateHarness(AppData data)
    {
        var store = new DataStore(data);
        var chatViewModel = new ChatViewModel(store, new CopilotService());
        var jobService = new BackgroundJobService(store, chatViewModel);
        return new TestHarness(new BackgroundJobsViewModel(store, jobService), jobService);
    }

    private static Chat CreateChat(string title)
    {
        return new Chat
        {
            Id = Guid.NewGuid(),
            Title = title,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private static BackgroundJob CreateJob(Guid chatId, string name)
    {
        return new BackgroundJob
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Name = name,
            Prompt = "Check something.",
            TriggerType = BackgroundJobTriggerTypes.Time,
            ScheduleType = BackgroundJobScheduleTypes.Interval,
            IsEnabled = false
        };
    }

    private sealed record TestHarness(BackgroundJobsViewModel ViewModel, BackgroundJobService JobService) : IDisposable
    {
        public void Dispose()
        {
            JobService.Dispose();
        }
    }
}
