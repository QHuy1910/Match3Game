using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuController : MonoBehaviour
{
    // Name of the menu scene to load when returning to menu
    public string MenuSceneName = "menue";

    public void StartGame()
    {
        SceneManager.LoadScene("mainGame"); // đổi "GameScene" thành tên scene game của bạn
    }

    // Call this from a UI Button to return to the main menu scene
    public void BackToMenu()
    {
        if (string.IsNullOrEmpty(MenuSceneName))
        {
            Debug.LogWarning("Menu scene name is not set on MenuController.");
            return;
        }

        // Check whether the scene can be loaded (exists in Build Settings or an AssetBundle)
        if (Application.CanStreamedLevelBeLoaded(MenuSceneName))
        {
            SceneManager.LoadScene(MenuSceneName);
        }
        else
        {
            Debug.LogError($"Scene '{MenuSceneName}' couldn't be loaded because it has not been added to the Build Settings or the AssetBundle has not been loaded.\nTo add a scene to the Build Settings use the menu File -> Build Settings... and add your scene to 'Scenes In Build'.");
        }
    }

    public void QuitGame()
    {
        Application.Quit();
        Debug.Log("Game exited"); // chỉ để test trong Editor
    }
}
