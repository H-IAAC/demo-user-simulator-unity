using UnityEngine;
using SFB;
using System.IO;
using System;

public class UploadConfiguration : MonoBehaviour
{
    [SerializeField] private ObjectsController objectsController;
    public event Action<string> ConfigurationLoaded;
    public string LatestConfigurationJson { get; private set; }

    void Start()
    {
        if (objectsController == null)
        {
            Debug.LogError("ButtonsController is not assigned in the inspector.");
            return;
        }
    }

    public void OpenFile()
    {
        objectsController.SetTextError(false);

        var paths = StandaloneFileBrowser.OpenFilePanel("Open File", "", "", false);

        if (paths != null && paths.Length > 0)
        {
            string filePath = paths[0];
            ProcessFile(filePath);
        }
    }
    
    private void ProcessFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError("Arquivo não encontrado: " + filePath);
            SetInvalidState();
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            Debug.LogError("Falha ao ler o arquivo: " + ex.Message);
            SetInvalidState();
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            Debug.LogError("JSON vazio: " + filePath);
            SetInvalidState();
            return;
        }

        if (!ConfigurationContract.TryParse(json, out ConfigurationContract.RootConfig config, out string parseError))
        {
            Debug.LogError(parseError);
            SetInvalidState();
            return;
        }

        if (!ConfigurationContract.Validate(config, out string validationError))
        {
            Debug.LogError($"JSON inválido ou incompleto: {validationError}");
            SetInvalidState();
            return;
        }

        objectsController.SetTextError(false);
        objectsController.SetTextAvailable(true);
        objectsController.SetBtnStartNotReady(false);
        objectsController.SetBtnStart(true);

        LatestConfigurationJson = json;
        ConfigurationLoaded?.Invoke(json);

        Debug.Log("UploadConfigurator: Configurações importadas de " + filePath);
    }

    private void SetInvalidState()
    {
        objectsController.SetTextError(true);
        objectsController.SetTextAvailable(false);
        objectsController.SetBtnStart(false);
        objectsController.SetBtnStartNotReady(true);
    }
}

public static class ConfigurationContract
{
    [Serializable]
    public class ApiConfig
    {
        public int cut;
        public int epochs;
    }

    [Serializable]
    public class SumoConfig
    {
        public string behaviour;
        public string[] locations;
    }

    [Serializable]
    public class RootConfig
    {
        public ApiConfig api;
        public SumoConfig sumo;
    }

    public static bool TryParse(string json, out RootConfig config, out string error)
    {
        config = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "JSON vazio";
            return false;
        }

        try
        {
            config = JsonUtility.FromJson<RootConfig>(json);
        }
        catch (Exception ex)
        {
            error = $"Falha ao ler JSON: {ex.Message}";
            return false;
        }

        if (config == null)
        {
            error = "objeto raiz nulo";
            return false;
        }

        error = null;
        return true;
    }

    public static bool Validate(RootConfig config, out string error)
    {
        if (config == null)
        {
            error = "objeto raiz nulo";
            return false;
        }

        if (config.api == null)
        {
            error = "campo api ausente";
            return false;
        }

        if (config.sumo == null)
        {
            error = "campo sumo ausente";
            return false;
        }

        if (config.api.cut <= 0 || config.api.epochs <= 0)
        {
            error = "api contém valores inválidos";
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.sumo.behaviour))
        {
            error = "sumo.behaviour ausente ou vazio";
            return false;
        }

        if (config.sumo.locations == null || config.sumo.locations.Length == 0)
        {
            error = "sumo.locations ausente ou vazio";
            return false;
        }

        for (int i = 0; i < config.sumo.locations.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(config.sumo.locations[i]))
            {
                error = $"sumo.locations[{i}] vazio";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static bool TryParseApiConfig(string json, out ApiConfig apiConfig, out string error)
    {
        apiConfig = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "api JSON vazio";
            return false;
        }

        try
        {
            apiConfig = JsonUtility.FromJson<ApiConfig>(json);
        }
        catch (Exception ex)
        {
            error = $"Falha ao ler api JSON: {ex.Message}";
            return false;
        }

        if (apiConfig == null)
        {
            error = "api JSON nulo";
            return false;
        }

        if (apiConfig.cut <= 0 || apiConfig.epochs <= 0)
        {
            error = "api contém valores inválidos";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryParseSumoConfig(string json, out SumoConfig sumoConfig, out string error)
    {
        sumoConfig = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "sumo JSON vazio";
            return false;
        }

        try
        {
            sumoConfig = JsonUtility.FromJson<SumoConfig>(json);
        }
        catch (Exception ex)
        {
            error = $"Falha ao ler sumo JSON: {ex.Message}";
            return false;
        }

        if (sumoConfig == null)
        {
            error = "sumo JSON nulo";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sumoConfig.behaviour))
        {
            error = "sumo.behaviour ausente ou vazio";
            return false;
        }

        if (sumoConfig.locations == null || sumoConfig.locations.Length == 0)
        {
            error = "sumo.locations ausente ou vazio";
            return false;
        }

        for (int i = 0; i < sumoConfig.locations.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(sumoConfig.locations[i]))
            {
                error = $"sumo.locations[{i}] vazio";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static bool TryParseFromRedisPayloads(string apiJson, string sumoJson, out RootConfig config, out string error)
    {
        config = null;

        if (!TryParseApiConfig(apiJson, out ApiConfig apiConfig, out string apiError))
        {
            error = apiError;
            return false;
        }

        if (!TryParseSumoConfig(sumoJson, out SumoConfig sumoConfig, out string sumoError))
        {
            error = sumoError;
            return false;
        }

        config = new RootConfig
        {
            api = apiConfig,
            sumo = sumoConfig
        };

        if (!Validate(config, out string validationError))
        {
            error = validationError;
            return false;
        }

        error = null;
        return true;
    }
}
