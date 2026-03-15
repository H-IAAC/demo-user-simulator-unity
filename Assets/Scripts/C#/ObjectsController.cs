using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

public class ObjectsController : MonoBehaviour
{

    [Header("Textos")]
    [SerializeField] private GameObject textAvailable;
    [SerializeField] private GameObject textError;
    [SerializeField] private GameObject textRunning;
    [SerializeField] private GameObject textFinish;
    

    [Header("Botões")]
    [SerializeField] private GameObject btnStart;
    [SerializeField] private GameObject btnStartNotReady;

    [Header("Barra de progresso")]
    [SerializeField] private GameObject progressBar;
    private Slider progressSliderCache;
    [SerializeField] private Image progressFill;
    [SerializeField] private TextMeshProUGUI progressLabel;

    [Header("Mapa")]
    [SerializeField] private GameObject OSMap;

    [Header("Gráficos")]
    [SerializeField] private GameObject plots;
    [SerializeField] private GameObject segmentationLegacy;

    [Header("Elementos HTML extras")]
    [SerializeField] private List<GameObject> extraHtmlElements = new List<GameObject>();

    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (textAvailable == null || 
            textError == null ||
            textRunning == null ||
            textFinish == null ||
            btnStart == null ||
            btnStartNotReady == null ||
            progressBar == null ||
            progressFill == null ||
            progressLabel == null ||
            OSMap == null ||
            plots == null
            )
        {
            Debug.LogError("One or more GameObjects are not assigned in the inspector.");
            return;
        }

        // Hidden objects at the beginning
        textAvailable.SetActive(false);
        textError.SetActive(false);
        textRunning.SetActive(false);
        textFinish.SetActive(false);
        btnStart.SetActive(false);
        progressBar.SetActive(false);
        progressLabel.gameObject.SetActive(false);
        OSMap.SetActive(false);
        plots.SetActive(false);
        if (segmentationLegacy != null)
            segmentationLegacy.SetActive(false);
        SetExtraHtmlElements(false);

        progressFill.color = new Color32(0xFF, 0x75, 0x1A, 0xFF);
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

    public void SetTextFinish(bool status)
    {
        textFinish.SetActive(status);
    }

    public void SetBtnStart(bool status)
    {
        btnStart.SetActive(status);
    }

    public void SetBtnStartNotReady(bool status)
    {
        btnStartNotReady.SetActive(status);
    }

    public void SetProgressBar(bool status)
    {
        progressBar.SetActive(status);
        progressLabel.gameObject.SetActive(status);
    }

    public void SetOSMap(bool status)
    {
        OSMap.SetActive(status);
    }

    public void SetPlots(bool status)
    {
        plots.SetActive(status);
        // Legacy support: if an old segmentation object still exists in scene, keep it in sync.
        if (segmentationLegacy != null)
            segmentationLegacy.SetActive(status);
    }

    public void SetSegmented(bool status)
    {
        // Segmentation is now unified with plots.
        SetPlots(status);
    }

    public void SetExtraHtmlElements(bool status)
    {
        if (extraHtmlElements == null)
            return;

        for (int i = 0; i < extraHtmlElements.Count; i++)
        {
            if (extraHtmlElements[i] != null)
                extraHtmlElements[i].SetActive(status);
        }
    }

    public void UpdateProgressBar(float percent)
    {
        float clamped = Mathf.Clamp(percent, 0f, 100f);

        if (progressSliderCache == null)
            progressSliderCache = progressBar.GetComponent<Slider>();
        if (progressSliderCache != null)
        {
            progressSliderCache.minValue = 0f;
            progressSliderCache.maxValue = 100f;
            progressSliderCache.value = clamped;
        }

        progressFill.fillAmount = clamped / 100f;
        progressLabel.text = $"{Mathf.RoundToInt(clamped)}%";

        Canvas.ForceUpdateCanvases();
    }

}
