using UnityEngine;
using System.Collections;

public class StartSimulation : MonoBehaviour
{
    [SerializeField] private ObjectsController objectsController;
 
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (objectsController == null)
        {
            Debug.LogError("ButtonsController is not assigned in the inspector.");
            return;
        }
    }

    public void StartSim()
    {
        StartCoroutine(StartSimRoutine());
    }

    private IEnumerator StartSimRoutine()
    {
        objectsController.SetBtnStart(false);
        objectsController.SetBtnStartNotReady(true);
        objectsController.SetTextAvailable(false);
        objectsController.SetTextRunning(true);
        objectsController.SetProgressBar(true);

        // espera o loading terminar
        yield return StartCoroutine(LoadingDummyRoutine());

        objectsController.SetTextRunning(false);
        objectsController.SetProgressBar(false);
        objectsController.SetTextFinish(true);
        objectsController.SetOSMap(true);
        objectsController.SetPlots(true);
        objectsController.SetSegmented(true);
    }

    private IEnumerator LoadingDummyRoutine()
    {
        float duration = 5f;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            float percent = Mathf.Lerp(0f, 100f, elapsed / duration);
            objectsController.UpdateProgressBar(percent);
            elapsed += Time.deltaTime;
            yield return null;
        }
        objectsController.UpdateProgressBar(100f);
    }
}
