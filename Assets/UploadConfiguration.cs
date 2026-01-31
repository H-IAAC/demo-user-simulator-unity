using UnityEngine;
using SFB;

public class UploadConfiguration : MonoBehaviour
{
    public void OpenFile()
    {
        var paths = StandaloneFileBrowser.OpenFilePanel("Open File", "", "", false);
        
        if (paths != null && paths.Length > 0)
        {
            string filePath = paths[0];
            Debug.Log("Arquivo selecionado: " + filePath);
            
            // ProcessFile(filePath);
        }
    }
    
    private void ProcessFile(string filePath)
    {
        Debug.Log("Configurações importadas de: " + filePath);
    }
}
