using System;
using System.Collections;
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

    private bool isRunning;

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

        objectsController.SetBtnStart(false);
        objectsController.SetBtnStartNotReady(true);
        objectsController.SetTextAvailable(false);
        objectsController.SetTextRunning(true);
        objectsController.SetProgressBar(true);
        objectsController.UpdateProgressBar(0f);

        yield return StartCoroutine(RunRoutineWithScheduler(RealSimulationRoutine()));

        objectsController.SetTextRunning(false);
        objectsController.SetProgressBar(false);
        objectsController.SetTextFinish(true);
        objectsController.SetOSMap(true);
        objectsController.SetPlots(true);
        objectsController.SetExtraHtmlElements(true);

        if (ultralightLocalServer != null)
        {
            ultralightLocalServer.ReloadConfiguredViews();
            ultralightLocalServer.SetAdditionalViewsVisible(true);
        }

        isRunning = false;
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
}
