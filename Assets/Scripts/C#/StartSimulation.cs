using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using HIAAC.CstUnity.Demo;

public class StartSimulation : MonoBehaviour
{
    [SerializeField] private ObjectsController objectsController;
    [SerializeField] private MemoryStorage memoryStorage;
    [SerializeField] private UltralightLocalServer ultralightLocalServer;

    [Header("Scheduler da barra")]
    [SerializeField, Min(0.5f)] private float targetRoutineTime = 5f;
    [SerializeField, Range(50f, 95f)] private float progressAtTargetTime = 85f;
    [SerializeField, Range(90f, 99.5f)] private float maxProgressBeforeFinish = 98f;
    [SerializeField, Min(0.05f)] private float overtimeSlowdown = 0.35f;
    [SerializeField, Min(0.1f)] private float slowdownTransitionWindow = 1.2f;
    [SerializeField, Min(0.1f)] private float initialLogStrength = 6f;
    [SerializeField, Min(0.1f)] private float overtimeLogStrength = 2.5f;
    [SerializeField, Min(0.01f)] private float progressSmoothingTime = 0.18f;
    [SerializeField, Min(0.05f)] private float finalSnapDuration = 0.3f;

    [Header("Simulação (dummy)")]
    [SerializeField, Min(0.1f)] private float simulatedRoutineDuration = 5f;

    [Header("Sincronização de HTML/UI")]
    [SerializeField] private bool enforceUiSyncBeforeFinish = true;
    [SerializeField, Min(0.5f)] private float waitForUiFilesTimeoutSeconds = 120f;
    [SerializeField, Min(0.05f)] private float uiFilesPollIntervalSeconds = 0.2f;
    [SerializeField, Min(0.1f)] private float uiFilesStableDurationSeconds = 0.8f;
    [SerializeField, Min(0f)] private float uiFilesTimestampSlackSeconds = 1f;
    [SerializeField, Min(0.5f)] private float waitForUltralightRefreshTimeoutSeconds = 20f;

    private bool isRunning;

    private struct FileSnapshot
    {
        public bool Exists;
        public long Length;
        public long LastWriteUtcTicks;

        public bool EqualsTo(FileSnapshot other)
        {
            return Exists == other.Exists &&
                   Length == other.Length &&
                   LastWriteUtcTicks == other.LastWriteUtcTicks;
        }
    }

    void Start()
    {
        if (objectsController == null)
        {
            Debug.LogError("ButtonsController is not assigned in the inspector.");
            return;
        }

        if (memoryStorage == null)
        {
            memoryStorage = FindAnyObjectByType<MemoryStorage>();
        }

        if (ultralightLocalServer == null)
        {
            ultralightLocalServer = FindAnyObjectByType<UltralightLocalServer>();
        }

        if (ultralightLocalServer != null)
        {
            ultralightLocalServer.SetAdditionalViewsVisible(false);
        }
    }

    public void StartSim()
    {
        if (isRunning)
            return;

        if (memoryStorage == null)
        {
            memoryStorage = FindAnyObjectByType<MemoryStorage>();
        }

        if (memoryStorage != null)
        {
            memoryStorage.SetSimulationStarted(true);
            Debug.Log($"[StartSimulation] SimulationStarted={memoryStorage.GetSimulationStarted()} (after Start click)");
        }
        else
        {
            Debug.LogWarning("[StartSimulation] MemoryStorage não encontrado. SimulationStarted não foi marcado como true.");
        }

        StartCoroutine(StartSimRoutine());
    }

    private IEnumerator StartSimRoutine()
    {
        isRunning = true;

        if (ultralightLocalServer != null)
            ultralightLocalServer.SetAdditionalViewsVisible(false);

        DateTime runStartedUtc = DateTime.UtcNow;
        Dictionary<string, FileSnapshot> baselineUiSnapshots = CaptureConfiguredUiFileSnapshots();

        objectsController.SetBtnStart(false);
        objectsController.SetBtnStartNotReady(true);
        objectsController.SetTextAvailable(false);
        objectsController.SetTextRunning(true);
        objectsController.SetProgressBar(true);
        objectsController.UpdateProgressBar(0f);

        yield return StartCoroutine(RunRoutineWithScheduler(RunSimulationAndUltralightSyncRoutine(runStartedUtc, baselineUiSnapshots)));

        objectsController.SetTextRunning(false);
        objectsController.SetProgressBar(false);
        objectsController.SetTextFinish(true);
        objectsController.SetOSMap(true);
        objectsController.SetPlots(true);
        objectsController.SetExtraHtmlElements(true);

        if (ultralightLocalServer != null)
        {
            ultralightLocalServer.StartServer();
            ultralightLocalServer.ReloadConfiguredViews();
            ultralightLocalServer.SetAdditionalViewsVisible(true);
        }

        isRunning = false;
    }

    private IEnumerator RunSimulationAndUltralightSyncRoutine(DateTime runStartedUtc, Dictionary<string, FileSnapshot> baselineUiSnapshots)
    {
        yield return StartCoroutine(RealSimulationRoutine());

        if (ultralightLocalServer == null)
            yield break;

        bool uiFilesReady = false;
        yield return StartCoroutine(WaitForUiFilesRefreshAndSettle(runStartedUtc, baselineUiSnapshots, ready => uiFilesReady = ready));

        // Keep plot containers active so Ultralight views can actually render during sync.
        objectsController.SetPlots(true);
        objectsController.SetExtraHtmlElements(true);

        List<Ultralight> targets = ultralightLocalServer.GetConfiguredUltralightTargets();
        Dictionary<Ultralight, int> baselineRenderRevisions = CaptureRenderRevisions(targets);

        ultralightLocalServer.StartServer();
        ultralightLocalServer.SetAdditionalViewsVisible(true);
        ultralightLocalServer.ReloadConfiguredViews();

        bool ultralightRefreshed = false;
        yield return StartCoroutine(WaitForUltralightRenderRefresh(targets, baselineRenderRevisions, refreshed => ultralightRefreshed = refreshed));

        if (!uiFilesReady && !enforceUiSyncBeforeFinish)
            Debug.LogWarning("[StartSimulation] Timeout aguardando novos HTML na pasta ui. Finalizando loading com o melhor estado disponível.");

        if (!ultralightRefreshed && !enforceUiSyncBeforeFinish)
            Debug.LogWarning("[StartSimulation] Timeout aguardando refresh visual do Ultralight. Finalizando loading com o melhor estado disponível.");
    }

    private IEnumerator RunRoutineWithScheduler(IEnumerator routine)
    {
        bool routineFinished = false;

        StartCoroutine(WrapRoutine(routine, () => routineFinished = true));
        yield return StartCoroutine(ProgressSchedulerRoutine(() => routineFinished));
    }

    private IEnumerator WrapRoutine(IEnumerator routine, Action onComplete)
    {
        yield return StartCoroutine(routine);
        onComplete?.Invoke();
    }

    private IEnumerator ProgressSchedulerRoutine(Func<bool> isRoutineFinished)
    {
        float elapsed = 0f;
        float lastPercent = 0f;
        float progressVelocity = 0f;

        float safeTargetTime = Mathf.Max(0.1f, targetRoutineTime);
        float safeProgressAtTarget = Mathf.Clamp(progressAtTargetTime, 0f, 99f);
        float safeMaxBeforeFinish = Mathf.Clamp(maxProgressBeforeFinish, safeProgressAtTarget + 0.1f, 99.8f);
        float safeSlowdown = Mathf.Max(0.01f, overtimeSlowdown);
        float safeTransitionWindow = Mathf.Max(0.1f, slowdownTransitionWindow);
        float safeInitialLogStrength = Mathf.Max(0.1f, initialLogStrength);
        float safeOvertimeLogStrength = Mathf.Max(0.1f, overtimeLogStrength);
        float safeProgressSmoothing = Mathf.Max(0.01f, progressSmoothingTime);
        float halfTransitionWindow = safeTransitionWindow * 0.5f;
        float transitionStart = safeTargetTime - halfTransitionWindow;
        float transitionEnd = safeTargetTime + halfTransitionWindow;

        while (!isRoutineFinished())
        {
            elapsed += Time.deltaTime;

            float initialRawT = Mathf.Clamp01(elapsed / safeTargetTime);
            float initialT = Mathf.SmoothStep(0f, 1f, initialRawT);
            float initialLogT = EvaluateInvertedLog01(initialT, safeInitialLogStrength);
            float initialPercent = Mathf.Lerp(0f, safeProgressAtTarget, initialLogT);

            float signedOvertime = elapsed - safeTargetTime;
            float overtimeElapsed = Mathf.Max(0f, signedOvertime);
            float overtimeLog = Mathf.Log(1f + overtimeElapsed * safeSlowdown * safeOvertimeLogStrength);
            float overtimeT = overtimeLog / (1f + overtimeLog);
            float overtimePercent = Mathf.Lerp(safeProgressAtTarget, safeMaxBeforeFinish, overtimeT);

            float transitionBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(transitionStart, transitionEnd, elapsed));
            float rawPercent = Mathf.Lerp(initialPercent, overtimePercent, transitionBlend);
            float smoothedPercent = Mathf.SmoothDamp(lastPercent, rawPercent, ref progressVelocity, safeProgressSmoothing);
            lastPercent = Mathf.Clamp(Mathf.Max(lastPercent, smoothedPercent), 0f, safeMaxBeforeFinish);

            objectsController.UpdateProgressBar(lastPercent);
            yield return null;
        }

        float safeSnapDuration = Mathf.Max(0.01f, finalSnapDuration);
        float snapElapsed = 0f;
        while (snapElapsed < safeSnapDuration)
        {
            snapElapsed += Time.deltaTime;
            float t = snapElapsed / safeSnapDuration;
            objectsController.UpdateProgressBar(Mathf.Lerp(lastPercent, 100f, t));
            yield return null;
        }

        objectsController.UpdateProgressBar(100f);
    }

    private float EvaluateInvertedLog01(float t, float strength)
    {
        float clampedT = Mathf.Clamp01(t);
        float safeStrength = Mathf.Max(0.0001f, strength);
        float numerator = Mathf.Log(1f + safeStrength * (1f - clampedT));
        float denominator = Mathf.Log(1f + safeStrength);
        return 1f - (numerator / denominator);
    }

    private IEnumerator RealSimulationRoutine()
    {
        // Trocar esta rotina pela rotina real quando estiver pronta.
        yield return new WaitForSeconds(simulatedRoutineDuration);
    }

    private Dictionary<string, FileSnapshot> CaptureConfiguredUiFileSnapshots()
    {
        Dictionary<string, FileSnapshot> snapshots = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (ultralightLocalServer == null)
            return snapshots;

        List<string> configuredPaths = ultralightLocalServer.GetConfiguredSourcePagePaths();
        for (int i = 0; i < configuredPaths.Count; i++)
        {
            string path = configuredPaths[i];
            if (string.IsNullOrWhiteSpace(path))
                continue;

            snapshots[path] = ReadSnapshot(path);
        }

        return snapshots;
    }

    private IEnumerator WaitForUiFilesRefreshAndSettle(DateTime runStartedUtc, Dictionary<string, FileSnapshot> baselineUiSnapshots, Action<bool> onComplete)
    {
        if (ultralightLocalServer == null)
        {
            onComplete?.Invoke(true);
            yield break;
        }

        List<string> configuredPaths = ultralightLocalServer.GetConfiguredSourcePagePaths();
        if (configuredPaths.Count == 0)
        {
            Debug.LogWarning("[StartSimulation] Nenhuma página HTML configurada no UltralightLocalServer para monitorar.");
            onComplete?.Invoke(true);
            yield break;
        }

        float timeout = Mathf.Max(0.5f, waitForUiFilesTimeoutSeconds);
        if (enforceUiSyncBeforeFinish)
            timeout = float.PositiveInfinity;
        float pollInterval = Mathf.Max(0.05f, uiFilesPollIntervalSeconds);
        float stableDuration = Mathf.Max(0.1f, uiFilesStableDurationSeconds);
        DateTime freshnessThreshold = runStartedUtc.AddSeconds(-Mathf.Max(0f, uiFilesTimestampSlackSeconds));

        float elapsed = 0f;
        float stableElapsed = 0f;
        Dictionary<string, FileSnapshot> stableCandidate = null;
        WaitForSeconds wait = new WaitForSeconds(pollInterval);

        while (elapsed < timeout)
        {
            if (TryCaptureReadableSnapshots(configuredPaths, out Dictionary<string, FileSnapshot> currentSnapshots))
            {
                bool changedFromBaseline = HasChangedFromBaseline(currentSnapshots, baselineUiSnapshots);
                bool allFresh = AreAllSnapshotsFresh(currentSnapshots, freshnessThreshold);

                if (changedFromBaseline || allFresh)
                {
                    if (stableCandidate != null && SnapshotsMatch(stableCandidate, currentSnapshots))
                    {
                        stableElapsed += pollInterval;
                    }
                    else
                    {
                        stableCandidate = currentSnapshots;
                        stableElapsed = 0f;
                    }

                    if (stableElapsed >= stableDuration)
                    {
                        onComplete?.Invoke(true);
                        yield break;
                    }
                }
                else
                {
                    stableCandidate = null;
                    stableElapsed = 0f;
                }
            }
            else
            {
                stableCandidate = null;
                stableElapsed = 0f;
            }

            yield return wait;
            elapsed += pollInterval;
        }

        onComplete?.Invoke(false);
    }

    private IEnumerator WaitForUltralightRenderRefresh(List<Ultralight> targets, Dictionary<Ultralight, int> baselineRenderRevisions, Action<bool> onComplete)
    {
        if (targets == null || targets.Count == 0)
        {
            onComplete?.Invoke(true);
            yield break;
        }

        List<Ultralight> pending = new List<Ultralight>();
        for (int i = 0; i < targets.Count; i++)
        {
            Ultralight target = targets[i];
            if (target == null || !target.gameObject.activeInHierarchy)
                continue;

            pending.Add(target);
        }

        if (pending.Count == 0)
        {
            onComplete?.Invoke(true);
            yield break;
        }

        float timeout = Mathf.Max(0.5f, waitForUltralightRefreshTimeoutSeconds);
        if (enforceUiSyncBeforeFinish)
            timeout = float.PositiveInfinity;
        float elapsed = 0f;

        yield return null;

        while (elapsed < timeout)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Ultralight target = pending[i];
                if (target == null)
                {
                    pending.RemoveAt(i);
                    continue;
                }

                if (!baselineRenderRevisions.TryGetValue(target, out int baseline))
                    baseline = -1;

                if (target.RenderRevision > baseline)
                    pending.RemoveAt(i);
            }

            if (pending.Count == 0)
            {
                onComplete?.Invoke(true);
                yield break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        onComplete?.Invoke(false);
    }

    private Dictionary<Ultralight, int> CaptureRenderRevisions(List<Ultralight> targets)
    {
        Dictionary<Ultralight, int> revisions = new Dictionary<Ultralight, int>();
        if (targets == null)
            return revisions;

        for (int i = 0; i < targets.Count; i++)
        {
            Ultralight target = targets[i];
            if (target == null || revisions.ContainsKey(target))
                continue;

            revisions[target] = target.RenderRevision;
        }

        return revisions;
    }

    private bool TryCaptureReadableSnapshots(List<string> paths, out Dictionary<string, FileSnapshot> snapshots)
    {
        snapshots = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (paths == null || paths.Count == 0)
            return true;

        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            FileSnapshot snapshot = ReadSnapshot(path);
            snapshots[path] = snapshot;

            if (!snapshot.Exists || snapshot.Length <= 0)
                return false;
        }

        return true;
    }

    private bool HasChangedFromBaseline(Dictionary<string, FileSnapshot> currentSnapshots, Dictionary<string, FileSnapshot> baselineSnapshots)
    {
        if (currentSnapshots == null || currentSnapshots.Count == 0)
            return false;

        foreach (KeyValuePair<string, FileSnapshot> pair in currentSnapshots)
        {
            if (baselineSnapshots == null || !baselineSnapshots.TryGetValue(pair.Key, out FileSnapshot baseline))
                return true;

            if (!pair.Value.EqualsTo(baseline))
                return true;
        }

        return false;
    }

    private bool AreAllSnapshotsFresh(Dictionary<string, FileSnapshot> snapshots, DateTime freshnessThresholdUtc)
    {
        if (snapshots == null || snapshots.Count == 0)
            return false;

        long thresholdTicks = freshnessThresholdUtc.Ticks;
        foreach (KeyValuePair<string, FileSnapshot> pair in snapshots)
        {
            if (!pair.Value.Exists || pair.Value.LastWriteUtcTicks < thresholdTicks)
                return false;
        }

        return true;
    }

    private bool SnapshotsMatch(Dictionary<string, FileSnapshot> a, Dictionary<string, FileSnapshot> b)
    {
        if (a == null || b == null || a.Count != b.Count)
            return false;

        foreach (KeyValuePair<string, FileSnapshot> pair in a)
        {
            if (!b.TryGetValue(pair.Key, out FileSnapshot snapshotB))
                return false;

            if (!pair.Value.EqualsTo(snapshotB))
                return false;
        }

        return true;
    }

    private FileSnapshot ReadSnapshot(string path)
    {
        FileSnapshot snapshot = new FileSnapshot
        {
            Exists = false,
            Length = 0,
            LastWriteUtcTicks = 0
        };

        if (string.IsNullOrWhiteSpace(path))
            return snapshot;

        try
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists)
                return snapshot;

            snapshot.Exists = true;
            snapshot.Length = info.Length;
            snapshot.LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            return snapshot;
        }
        catch
        {
            return snapshot;
        }
    }
}
