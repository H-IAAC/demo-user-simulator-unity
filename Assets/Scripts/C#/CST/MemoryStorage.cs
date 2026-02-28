using UnityEngine;
using HIAAC.CstUnity.Core.Entities;
using StackExchange.Redis;
using HIAAC.CstUnity.MemoryStorage;

namespace HIAAC.CstUnity.Demo
{
    // MemoryStorage with 4 main memories:
    // - ApiConfiguration
    // - SumoConfiguration
    // - GPSBuffer
    // - Episodes
    public class MemoryStorage : MonoBehaviour
    {
        [Header("Redis")]
        [SerializeField] private string redisHost = "localhost";
        [SerializeField] private int redisPort = 6379;
        [SerializeField] private bool flushOnStart = false;
        [SerializeField] private bool abortOnConnectFail = false;

        [Header("Codelet")]
        [SerializeField] private int memoryStorageTimeStepMs = 50;

        private Mind mind;
        private MemoryStorageCodelet memoryStorageCodelet;

        private Memory apiConfiguration;
        private Memory sumoConfiguration;
        private Memory gpsBuffer;
        private Memory episodes;

        public Memory ApiConfiguration => apiConfiguration;
        public Memory SumoConfiguration => sumoConfiguration;
        public Memory GPSBuffer => gpsBuffer;
        public Memory Episodes => episodes;

        private void Start()
        {
            if (flushOnStart)
            {
                var muxer = ConnectionMultiplexer.Connect($"{redisHost}:{redisPort},allowAdmin=true,abortConnect={abortOnConnectFail.ToString().ToLower()}");
                var server = muxer.GetServer(redisHost, redisPort);
                server.FlushAllDatabases();
            }

            mind = new Mind();

            apiConfiguration = mind.createMemoryObject("ApiConfiguration", "");
            sumoConfiguration = mind.createMemoryObject("SumoConfiguration", "");
            gpsBuffer = mind.createMemoryObject("GPSBuffer", "");
            episodes = mind.createMemoryObject("Episodes", "");

            try
            {
                memoryStorageCodelet = new MemoryStorageCodelet(
                    mind,
                    "memory_storage",
                    "default_mind",
                    0.5,
                    $"{redisHost}:{redisPort},abortConnect={abortOnConnectFail.ToString().ToLower()}"
                );
                memoryStorageCodelet.setTimeStep(memoryStorageTimeStepMs);
                mind.insertCodelet(memoryStorageCodelet);
            }
            catch (RedisConnectionException ex)
            {
                Debug.LogError($"[MemoryStorage] Redis unavailable ({redisHost}:{redisPort}). Details: {ex.Message}");
                enabled = false;
                return;
            }
            mind.start();

            Debug.Log("MemoryStorage inicializado com memórias: ApiConfiguration, SumoConfiguration, GPSBuffer e Episodes.");
        }

        public void SetApiConfiguration(object value)
        {
            apiConfiguration.setI(value);
        }

        public void SetSumoConfiguration(object value)
        {
            sumoConfiguration.setI(value);
        }

        public void SetGPSBuffer(object value)
        {
            gpsBuffer.setI(value);
        }

        public void SetEpisodes(object value)
        {
            episodes.setI(value);
        }

        public object GetApiConfiguration()
        {
            return apiConfiguration.getI();
        }

        public object GetSumoConfiguration()
        {
            return sumoConfiguration.getI();
        }

        public object GetGPSBuffer()
        {
            return gpsBuffer.getI();
        }

        public object GetEpisodes()
        {
            return episodes.getI();
        }

        public bool TryGetApiConfiguration(out ConfigurationContract.ApiConfig apiConfig, out string error)
        {
            string apiJson = GetApiConfiguration()?.ToString();
            return ConfigurationContract.TryParseApiConfig(apiJson, out apiConfig, out error);
        }

        public bool TryGetSumoConfiguration(out ConfigurationContract.SumoConfig sumoConfig, out string error)
        {
            string sumoJson = GetSumoConfiguration()?.ToString();
            return ConfigurationContract.TryParseSumoConfig(sumoJson, out sumoConfig, out error);
        }

        public bool TryGetConfiguration(out ConfigurationContract.RootConfig config, out string error)
        {
            string apiJson = GetApiConfiguration()?.ToString();
            string sumoJson = GetSumoConfiguration()?.ToString();
            return ConfigurationContract.TryParseFromRedisPayloads(apiJson, sumoJson, out config, out error);
        }
    }
}
