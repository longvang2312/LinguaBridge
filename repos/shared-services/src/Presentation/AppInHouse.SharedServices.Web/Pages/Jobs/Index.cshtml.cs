using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages.Jobs;

public class IndexModel(ISharedServicesClient client, ILogger<IndexModel> logger) : PageModel
{
    public IReadOnlyList<JobQueueStatusDto> Queues { get; private set; } = [];
    public IReadOnlyList<ScheduledTaskDto> CronJobs { get; private set; } = [];

    public int TotalPending => Queues.Sum(q => q.Pending);
    public int TotalProcessing => Queues.Sum(q => q.Processing);
    public int TotalCompleted => Queues.Sum(q => q.CompletedLast24h);
    public int TotalFailed => Queues.Sum(q => q.Failed);

    public string? LoadError { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Queues = await client.GetJobQueuesAsync(cancellationToken);
            CronJobs = await client.GetJobSchedulesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not load the job queues and schedules.");
            LoadError = UiHelpers.Describe(ex);
        }
    }

    public async Task<IActionResult> OnPostTriggerAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var task = await client.RunScheduledTaskAsync(id, cancellationToken);
            Message = $"Đã chạy tác vụ \"{task.Name}\": {task.LastStatus ?? "đang chạy"}";
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not run the scheduled task {Task}.", id);
            ErrorMessage = UiHelpers.Describe(ex);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync(string queue, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.RetryFailedJobsAsync(queue, cancellationToken);
            Message = result.Requeued == 0
                ? $"Hàng đợi {queue} không có tác vụ lỗi."
                : $"Đã đưa {result.Requeued} tác vụ lỗi của hàng đợi {queue} vào chạy lại.";
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not retry the failed jobs of queue {Queue}.", queue);
            ErrorMessage = UiHelpers.Describe(ex);
        }

        return RedirectToPage();
    }
}
