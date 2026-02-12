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
        private string latestConfigJson;
        private int configVersion;

        private Mind mind;
        private Memory sumConfiguration;
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
            sumConfiguration = mind.createMemoryObject("SumConfiguration", "");

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

            sumoConfiguratorCodelet = new SumoConfiguratorCodelet(ReadLatestConfiguration, ReadVersion);
            sumoConfiguratorCodelet.setTimeStep(timeStepMs);
            sumoConfiguratorCodelet.addOutput(sumConfiguration);
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
            lock (configLock)
            {
                latestConfigJson = json;
                configVersion++;
            }
        }

        private string ReadLatestConfiguration()
        {
            lock (configLock)
            {
                return latestConfigJson;
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
            private readonly System.Func<string> getConfig;
            private readonly System.Func<int> getVersion;

            private Memory outputSumConfiguration;
            private int lastPublishedVersion = -1;

            public SumoConfiguratorCodelet(System.Func<string> getConfig, System.Func<int> getVersion)
            {
                this.getConfig = getConfig;
                this.getVersion = getVersion;
            }

            public override void accessMemoryObjects()
            {
                outputSumConfiguration = getOutput("SumConfiguration", 0);
            }

            public override void calculateActivation()
            {
                activation = 1.0f;
            }

            public override void proc()
            {
                if (outputSumConfiguration == null)
                    return;

                int version = getVersion();
                if (version == lastPublishedVersion)
                    return;

                string json = getConfig();
                if (string.IsNullOrWhiteSpace(json))
                    return;

                outputSumConfiguration.setI(json);
                lastPublishedVersion = version;

                Debug.Log("[SumoConfigurator] SumConfiguration updated from uploaded config.");
            }
        }
    }
}
