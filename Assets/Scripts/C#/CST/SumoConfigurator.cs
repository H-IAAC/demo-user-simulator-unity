using UnityEngine;
using HIAAC.CstUnity.Core.Entities;
using HIAAC.CstUnity.MemoryStorage;
using StackExchange.Redis;

namespace HIAAC.CstUnity.Demo
{
    public class SumoConfigurator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private UploadConfiguration uploadConfiguration;

        [Header("Redis / Mind")]
        [SerializeField] private string redisConnectionString = "localhost,abortConnect=false";
        [SerializeField] private string nodeName = "sumo_configurator";
        [SerializeField] private string mindName = "default_mind";
        [SerializeField] private int timeStepMs = 100;

        private readonly object configLock = new();
        private string latestApiConfigJson;
        private string latestSumoConfigJson;
        private int configVersion;

        private Mind mind;
        private Memory apiConfiguration;
        private Memory sumoConfiguration;
        private MemoryStorageCodelet memoryStorageCodelet;
        private SumoConfiguratorCodelet sumoConfiguratorCodelet;

        private void Start()
        {
            if (uploadConfiguration == null)
            {
                Debug.LogError("UploadConfiguration is not assigned in the inspector.");
                return;
            }

            uploadConfiguration.ConfigurationLoaded += OnConfigurationLoaded;

            mind = new Mind();
            apiConfiguration = mind.createMemoryObject("ApiConfiguration", "");
            sumoConfiguration = mind.createMemoryObject("SumoConfiguration", "");

            try
            {
                memoryStorageCodelet = new MemoryStorageCodelet(
                    mind,
                    nodeName,
                    mindName,
                    0.5,
                    redisConnectionString
                );
                memoryStorageCodelet.setTimeStep(timeStepMs);
                mind.insertCodelet(memoryStorageCodelet);
            }
            catch (RedisConnectionException ex)
            {
                Debug.LogError($"[SumoConfigurator] Redis unavailable ({redisConnectionString}). Details: {ex.Message}");
                enabled = false;
                return;
            }

            sumoConfiguratorCodelet = new SumoConfiguratorCodelet(ReadLatestApiConfiguration, ReadLatestSumoConfiguration, ReadVersion);
            sumoConfiguratorCodelet.setTimeStep(timeStepMs);
            sumoConfiguratorCodelet.addOutput(apiConfiguration);
            sumoConfiguratorCodelet.addOutput(sumoConfiguration);
            mind.insertCodelet(sumoConfiguratorCodelet);

            mind.start();

            if (!string.IsNullOrWhiteSpace(uploadConfiguration.LatestConfigurationJson))
            {
                OnConfigurationLoaded(uploadConfiguration.LatestConfigurationJson);
            }
        }

        private void OnDestroy()
        {
            if (uploadConfiguration != null)
            {
                uploadConfiguration.ConfigurationLoaded -= OnConfigurationLoaded;
            }

            mind?.shutDown();
        }

        private void OnConfigurationLoaded(string json)
        {
            if (!ConfigurationContract.TryParse(json, out ConfigurationContract.RootConfig config, out string parseError))
            {
                Debug.LogError($"[SumoConfigurator] {parseError}");
                return;
            }

            if (!ConfigurationContract.Validate(config, out string validationError))
            {
                Debug.LogError($"[SumoConfigurator] JSON inválido: {validationError}");
                return;
            }

            lock (configLock)
            {
                latestApiConfigJson = JsonUtility.ToJson(config.api);
                latestSumoConfigJson = JsonUtility.ToJson(config.sumo);
                configVersion++;
            }
        }

        private string ReadLatestApiConfiguration()
        {
            lock (configLock)
            {
                return latestApiConfigJson;
            }
        }

        private string ReadLatestSumoConfiguration()
        {
            lock (configLock)
            {
                return latestSumoConfigJson;
            }
        }

        private int ReadVersion()
        {
            lock (configLock)
            {
                return configVersion;
            }
        }

        private class SumoConfiguratorCodelet : Codelet
        {
            private readonly System.Func<string> getApiConfig;
            private readonly System.Func<string> getSumoConfig;
            private readonly System.Func<int> getVersion;

            private Memory outputApiConfiguration;
            private Memory outputSumoConfiguration;
            private int lastPublishedVersion = -1;

            public SumoConfiguratorCodelet(System.Func<string> getApiConfig, System.Func<string> getSumoConfig, System.Func<int> getVersion)
            {
                this.getApiConfig = getApiConfig;
                this.getSumoConfig = getSumoConfig;
                this.getVersion = getVersion;
            }

            public override void accessMemoryObjects()
            {
                outputApiConfiguration = getOutput("ApiConfiguration", 0);
                outputSumoConfiguration = getOutput("SumoConfiguration", 0);
            }

            public override void calculateActivation()
            {
                activation = 1.0f;
            }

            public override void proc()
            {
                if (outputApiConfiguration == null || outputSumoConfiguration == null)
                    return;

                int version = getVersion();
                if (version == lastPublishedVersion)
                    return;

                string apiJson = getApiConfig();
                string sumoJson = getSumoConfig();
                if (string.IsNullOrWhiteSpace(apiJson) || string.IsNullOrWhiteSpace(sumoJson))
                    return;

                outputApiConfiguration.setI(apiJson);
                outputSumoConfiguration.setI(sumoJson);
                lastPublishedVersion = version;

                Debug.Log("[SumoConfigurator] ApiConfiguration and SumoConfiguration updated from uploaded config.");
            }
        }
    }
}
