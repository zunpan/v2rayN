namespace ServiceLib.Manager;

public class TaskManager
{
    private static readonly Lazy<TaskManager> _instance = new(() => new());
    public static TaskManager Instance => _instance.Value;
    private Config _config;
    private Func<bool, string, Task>? _updateFunc;
    private readonly SemaphoreSlim _autoMixedTestTaskLock = new(1, 1);
    private AutoMixedTestCleanupService? _autoMixedTestCleanupService;

    public void RegUpdateTask(Config config, Func<bool, string, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;
        _autoMixedTestCleanupService = new AutoMixedTestCleanupService(_config);

        Task.Run(ScheduledTasks);
    }

    private async Task ScheduledTasks()
    {
        Logging.SaveLog("Setup Scheduled Tasks");

        var numOfExecuted = 1;
        while (true)
        {
            //1 minute
            await Task.Delay(1000 * 60);

            //Execute once 1 minute
            try
            {
                await UpdateTaskRunSubscription();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ScheduledTasks - UpdateTaskRunSubscription", ex);
            }

            try
            {
                await UpdateTaskRunAutoMixedTest();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ScheduledTasks - UpdateTaskRunAutoMixedTest", ex);
            }

            //Execute once 20 minute
            if (numOfExecuted % 20 == 0)
            {
                //Logging.SaveLog("Execute save config");

                try
                {
                    await ConfigHandler.SaveConfig(_config);
                    await ProfileExManager.Instance.SaveTo();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - SaveConfig", ex);
                }
            }

            //Execute once 1 hour
            if (numOfExecuted % 60 == 0)
            {
                //Logging.SaveLog("Execute delete expired files");

                FileUtils.DeleteExpiredFiles(Utils.GetBinConfigPath(), DateTime.Now.AddHours(-1));
                FileUtils.DeleteExpiredFiles(Utils.GetLogPath(), DateTime.Now.AddMonths(-1));
                FileUtils.DeleteExpiredFiles(Utils.GetTempPath(), DateTime.Now.AddMonths(-1));

                try
                {
                    await UpdateTaskRunGeo(numOfExecuted / 60);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - UpdateTaskRunGeo", ex);
                }
            }

            numOfExecuted++;
        }
    }

    private async Task UpdateTaskRunSubscription()
    {
        var updateTime = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
        var lstSubs = (await AppManager.Instance.SubItems())?
            .Where(t => t.AutoUpdateInterval > 0)
            .Where(t => updateTime - t.UpdateTime >= t.AutoUpdateInterval * 60)
            .ToList();

        if (lstSubs is not { Count: > 0 })
        {
            return;
        }

        Logging.SaveLog("Execute update subscription");

        foreach (var item in lstSubs)
        {
            await SubscriptionHandler.UpdateProcess(_config, item.Id, true, async (success, msg) =>
            {
                await _updateFunc?.Invoke(success, msg);
                if (success)
                {
                    Logging.SaveLog($"Update subscription end. {msg}");
                }
            });
            item.UpdateTime = updateTime;
            await ConfigHandler.AddSubItem(_config, item);
            await Task.Delay(1000);
        }
    }

    private async Task UpdateTaskRunGeo(int hours)
    {
        if (_config.GuiItem.AutoUpdateInterval > 0 && hours > 0 && hours % _config.GuiItem.AutoUpdateInterval == 0)
        {
            Logging.SaveLog("Execute update geo files");

            await new UpdateService(_config, async (success, msg) =>
            {
                await _updateFunc?.Invoke(false, msg);
            }).UpdateGeoFileAll();
        }
    }

    private async Task UpdateTaskRunAutoMixedTest()
    {
        if (_autoMixedTestCleanupService == null || !_config.SpeedTestItem.AutoMixedTestEnabled)
        {
            return;
        }

        if (!await _autoMixedTestTaskLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var nowUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
            var intervalMinutes = Math.Max(1, _config.SpeedTestItem.AutoMixedTestIntervalMinutes);
            if (_config.SpeedTestItem.AutoMixedTestLastRunTime > 0
                && nowUnix - _config.SpeedTestItem.AutoMixedTestLastRunTime < intervalMinutes * 60)
            {
                return;
            }

            _config.SpeedTestItem.AutoMixedTestLastRunTime = nowUnix;
            var result = await _autoMixedTestCleanupService.RunOnceAsync(_config.SubIndexId);
            await ConfigHandler.SaveConfig(_config);

            if (result.Skipped)
            {
                await _updateFunc?.Invoke(false, $"Auto mixed test skipped: {result.SkipReason}");
                return;
            }

            if (result.DeletedCount > 0)
            {
                var msg = $"Auto mixed test deleted {result.DeletedCount} nodes, sorted {result.SortedCount}";
                if (result.SwitchedToIndexId.IsNotEmpty())
                {
                    msg += ", switched to top node";
                }
                await _updateFunc?.Invoke(true, msg);
            }
            else if (result.Errors.Count > 0)
            {
                await _updateFunc?.Invoke(false, $"Auto mixed test finished with {result.Errors.Count} errors");
            }
            else
            {
                var msg = $"Auto mixed test finished. scanned={result.ScannedCount}, sorted={result.SortedCount}";
                if (result.SwitchedToIndexId.IsNotEmpty())
                {
                    msg += ", switched to top node";
                }
                await _updateFunc?.Invoke(true, msg);
            }
        }
        finally
        {
            _autoMixedTestTaskLock.Release();
        }
    }
}
