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
    public class SumoSimulationConfig
    {
        public int time_to_teleport;
        public float lateral_resolution;
        public float step_length;
        public int end_time;
        public int end_time_random_trips;
    }

    [Serializable]
    public class SumoBehaviourConfig
    {
        public SumoSimulationConfig simulation;
        public SumoVehicleConfig vehicle;
        public string[] locations;
    }

    [Serializable]
    public class SumoVehicleConfig
    {
        public float maxSpeed;
        public float accel;
        public float decel;
        public float speedFactor;
        public float minGap;
        public float emergencyDecel;
        public string vClass;
    }

    [Serializable]
    public class SumoConfig
    {
        public SumoBehaviourConfig behaviour;
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

        if (!ValidateApi(config.api, out error))
            return false;

        if (!ValidateSumo(config.sumo, out error))
            return false;

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

        return ValidateApi(apiConfig, out error);
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

        return ValidateSumo(sumoConfig, out error);
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

    private static bool ValidateApi(ApiConfig api, out string error)
    {
        if (api == null)
        {
            error = "campo api ausente";
            return false;
        }

        if (api.cut <= 0 || api.epochs <= 0)
        {
            error = "api contém valores inválidos";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateSumo(SumoConfig sumo, out string error)
    {
        if (sumo == null)
        {
            error = "campo sumo ausente";
            return false;
        }

        if (sumo.behaviour == null)
        {
            error = "sumo.behaviour ausente";
            return false;
        }

        if (!ValidateSimulation(sumo.behaviour.simulation, out error))
            return false;

        if (!ValidateVehicle(sumo.behaviour.vehicle, out error))
            return false;

        if (sumo.behaviour.locations == null || sumo.behaviour.locations.Length == 0)
        {
            error = "sumo.behaviour.locations ausente ou vazio";
            return false;
        }

        for (int i = 0; i < sumo.behaviour.locations.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(sumo.behaviour.locations[i]))
            {
                error = $"sumo.behaviour.locations[{i}] vazio";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool ValidateSimulation(SumoSimulationConfig simulation, out string error)
    {
        if (simulation == null)
        {
            error = "sumo.behaviour.simulation ausente";
            return false;
        }

        if (simulation.time_to_teleport <= 0 ||
            simulation.lateral_resolution <= 0f ||
            simulation.step_length <= 0f ||
            simulation.end_time <= 0 ||
            simulation.end_time_random_trips <= 0)
        {
            error = "sumo.behaviour.simulation contém valores inválidos";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateVehicle(SumoVehicleConfig vehicle, out string error)
    {
        if (vehicle == null)
        {
            error = "sumo.behaviour.vehicle ausente";
            return false;
        }

        if (vehicle.maxSpeed <= 0f ||
            vehicle.accel <= 0f ||
            vehicle.decel <= 0f ||
            vehicle.speedFactor <= 0f ||
            vehicle.minGap < 0f ||
            vehicle.emergencyDecel <= 0f)
        {
            error = "sumo.behaviour.vehicle contém valores inválidos";
            return false;
        }

        if (string.IsNullOrWhiteSpace(vehicle.vClass))
        {
            error = "sumo.behaviour.vehicle.vClass ausente ou vazio";
            return false;
        }

        error = null;
        return true;
    }
}
