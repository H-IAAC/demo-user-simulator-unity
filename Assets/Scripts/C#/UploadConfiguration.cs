using UnityEngine;
using SFB;
using System.IO;

public class UploadConfiguration : MonoBehaviour
{
    [SerializeField] private ButtonsController buttonsController;
    
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

    void Start()
    {
        if (buttonsController == null)
        {
            Debug.LogError("ButtonsController is not assigned in the inspector.");
            return;
        }
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
            buttonsController.SetTextError(true);
            return;
        }

        var json = File.ReadAllText(filePath);
        var config = JsonUtility.FromJson<RootConfig>(json);

        if (config == null || config.api == null || config.sumo == null)
        {
            Debug.LogError("JSON inválido ou incompleto: " + filePath);
            buttonsController.SetTextError(true);
            return;
        }

        buttonsController.SetBtnStartNotReady(false);
        buttonsController.SetBtnStart(true);

        Debug.Log("Configurações importadas de: " + filePath);
        Debug.Log("API -> cut: " + config.api.cut + ", epochs: " + config.api.epochs);
        Debug.Log("SUMO -> Behavior: " + config.sumo.Behavior);
    }
}
