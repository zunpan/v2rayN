using ServiceLib.Events;

namespace ServiceLib.Services;

public class AutoMixedTestCleanupService(Config config)
{
    private readonly Config _config = config;

    public async Task<AutoMixedTestRunResult> RunOnceAsync(string? subId)
    {
        var result = new AutoMixedTestRunResult();

        try
        {
            var profileModels = await AppManager.Instance.ProfileModels(subId, "");
            if (profileModels.Count <= 0)
            {
                result.Skipped = true;
                result.SkipReason = "no profiles";
                return result;
            }

            var orderedModels = profileModels.OrderBy(t => t.Sort).ToList();
            var allIndexIds = orderedModels
                .Where(t => t.IndexId.IsNotEmpty())
                .Select(t => t.IndexId)
                .ToList();
            var profileMap = await AppManager.Instance.GetProfileItemsByIndexIdsAsMap(allIndexIds);

            var testCandidates = orderedModels
                .Where(CanAutoMixedTest)
                .Select(t => profileMap.GetValueOrDefault(t.IndexId))
                .Where(t => t != null)
                .ToList();

            if (testCandidates.Count <= 0)
            {
                result.Skipped = true;
                result.SkipReason = "no testable profiles";
                return result;
            }

            result.ScannedCount = testCandidates.Count;

            var speedtestService = new SpeedtestService(_config, result =>
            {
                AppEvents.SpeedTestResultUpdated.Publish(result);
                return Task.CompletedTask;
            });
            await speedtestService.RunOnceAsync(ESpeedActionType.Mixedtest, testCandidates);

            var profileExMap = await GetProfileExMap();
            var invalidProfiles = testCandidates
                .Where(t =>
                {
                    var ex = profileExMap.GetValueOrDefault(t.IndexId);
                    var delay = ex?.Delay ?? 0;
                    var speed = ex?.Speed ?? 0;
                    return delay <= 0 || speed <= 0;
                })
                .ToList();

            if (invalidProfiles.Count > 0)
            {
                await ConfigHandler.RemoveServers(_config, invalidProfiles);
                result.DeletedCount = invalidProfiles.Count;
            }

            var sortedModels = await SortProfiles(subId);
            result.SortedCount = result.ScannedCount - result.DeletedCount;

            var latestModels = sortedModels.Count > 0 ? sortedModels : await AppManager.Instance.ProfileModels(subId, "");
            if (_config.SpeedTestItem.AutoSwitchBestNodeEnabled)
            {
                var target = latestModels.FirstOrDefault(t => t.IndexId.IsNotEmpty());
                if (target?.IndexId.IsNotEmpty() == true)
                {
                    result.SwitchedToIndexId = target.IndexId;
                    await ConfigHandler.SetDefaultServerIndex(_config, target.IndexId);
                    AppEvents.ProfilesRefreshRequested.Publish();
                    AppEvents.SetDefaultServerRequested.Publish(target.IndexId);
                    AppEvents.ReloadRequested.Publish();
                }
            }
            else if (result.DeletedCount > 0 && invalidProfiles.Any(t => t.IndexId == _config.IndexId))
            {
                await ConfigHandler.SetDefaultServer(_config, latestModels);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
            Logging.SaveLog(nameof(AutoMixedTestCleanupService), ex);
        }

        return result;
    }

    private async Task<List<ProfileItemModel>> SortProfiles(string? subId)
    {
        var profileModels = await AppManager.Instance.ProfileModels(subId, "");
        if (profileModels.Count <= 0)
        {
            return [];
        }

        var profileExMap = await GetProfileExMap();
        var eligible = profileModels
            .Where(CanAutoMixedTest)
            .Select(t => new
            {
                Model = t,
                Ex = profileExMap.GetValueOrDefault(t.IndexId)
            })
            .Where(t => t.Ex != null)
            .OrderBy(t => t.Ex!.Delay)
            .ThenByDescending(t => t.Ex!.Speed)
            .ThenBy(t => t.Ex!.Sort)
            .Select(t => t.Model)
            .ToList();

        var eligibleIds = eligible.Select(t => t.IndexId).ToHashSet();
        var others = profileModels
            .Where(t => !eligibleIds.Contains(t.IndexId))
            .OrderBy(t => profileExMap.GetValueOrDefault(t.IndexId)?.Sort ?? 0)
            .ToList();

        var finalOrder = eligible.Concat(others).ToList();
        for (var i = 0; i < finalOrder.Count; i++)
        {
            ProfileExManager.Instance.SetSort(finalOrder[i].IndexId, (i + 1) * 10);
        }
        await ProfileExManager.Instance.SaveTo();
        return finalOrder;
    }

    private static bool CanAutoMixedTest(ProfileItem profile)
    {
        return profile.IndexId.IsNotEmpty()
               && profile.ConfigType != EConfigType.Custom
               && (profile.ConfigType.IsComplexType() || profile.Port > 0);
    }

    private static bool CanAutoMixedTest(ProfileItemModel profile)
    {
        return profile.IndexId.IsNotEmpty()
               && profile.ConfigType != EConfigType.Custom
               && (profile.ConfigType.IsComplexType() || profile.Port > 0);
    }

    private static async Task<Dictionary<string, ProfileExItem>> GetProfileExMap()
    {
        var profileExs = await ProfileExManager.Instance.GetProfileExs();
        return profileExs
            .Where(t => t.IndexId.IsNotEmpty())
            .GroupBy(t => t.IndexId)
            .ToDictionary(t => t.Key, t => t.First());
    }

}
