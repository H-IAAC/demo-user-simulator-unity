using UnityEngine;

public class ButtonsController : MonoBehaviour
{

    [SerializeField] private GameObject textAvailable;
    [SerializeField] private GameObject textError;
    [SerializeField] private GameObject textRunning;

    [SerializeField] private GameObject btnStart;
    [SerializeField] private GameObject btnStartNotReady;

    [SerializeField] private GameObject OSMap;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (textAvailable == null || textError == null || textRunning == null || btnStart == null || btnStartNotReady == null || OSMap == null)
        {
            Debug.LogError("One or more GameObjects are not assigned in the inspector.");
            return;
        }

        // Hidden objects at the beginning
        textAvailable.SetActive(false);
        textError.SetActive(false);
        textRunning.SetActive(false);
        btnStart.SetActive(false);
        OSMap.SetActive(false);
    }

    public void SetTextAvailable(bool status)
    {
        textAvailable.SetActive(status);
    }

    public void SetTextError(bool status)
    {
        textError.SetActive(status);
    }

    public void SetTextRunning(bool status)
    {
        textRunning.SetActive(status);
    }


    public void SetBtnStart(bool status)
    {
        btnStart.SetActive(status);
    }

    public void SetBtnStartNotReady(bool status)
    {
        btnStartNotReady.SetActive(status);
    }

    public void SetOSMap(bool status)
    {
        OSMap.SetActive(status);
    }

}
