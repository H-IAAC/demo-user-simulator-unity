using UnityEngine;
using SFB;
using System.IO;

public class UploadConfiguration : MonoBehaviour
{
    [SerializeField] private GameObject errorGroup;
    [SerializeField] private GameObject availableGroup;
    [System.Serializable]
    private class ApiConfig
    {
        public int cut;
        public int epochs;
    }

    [System.Serializable]
    private class SumoConfig
    {
        public string Behavior;
    }

    [System.Serializable]
    private class RootConfig
    {
        public ApiConfig api;
        public SumoConfig sumo;
    }

    public void OpenFile()
    {
        var paths = StandaloneFileBrowser.OpenFilePanel("Open File", "", "", false);
        
        if (paths != null && paths.Length > 0)
        {
            string filePath = paths[0];
            Debug.Log("Arquivo selecionado: " + filePath);
            ProcessFile(filePath);
        }
    }
    
    private void ProcessFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError("Arquivo não encontrado: " + filePath);
            ShowError();
            return;
        }

        var json = File.ReadAllText(filePath);
        var config = JsonUtility.FromJson<RootConfig>(json);

        if (config == null || config.api == null || config.sumo == null)
        {
            Debug.LogError("JSON inválido ou incompleto: " + filePath);
            ShowError();
            return;
        }

        HideError();
        Debug.Log("Configurações importadas de: " + filePath);
        Debug.Log("API -> cut: " + config.api.cut + ", epochs: " + config.api.epochs);
        Debug.Log("SUMO -> Behavior: " + config.sumo.Behavior);
    }

    private void ShowError()
    {
        if (errorGroup != null)
        {
            errorGroup.SetActive(true);
            availableGroup.SetActive(false);
        }
    }

    private void HideError()
    {
        if (errorGroup != null)
        {
            errorGroup.SetActive(false);
            availableGroup.SetActive(true);
        }
    }

    void Start()
    {
        if (errorGroup != null)
        {
            errorGroup.SetActive(false);
        }
        if (availableGroup != null)
        {
            availableGroup.SetActive(false);
        }
    }

}
