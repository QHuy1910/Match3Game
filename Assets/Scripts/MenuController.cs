using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuController : MonoBehaviour
{
    public void StartGame()
    {
        SceneManager.LoadScene("mainGame"); // đổi "GameScene" thành tên scene game của bạn
    }

    public void QuitGame()
    {
        Application.Quit();
        Debug.Log("Game exited"); // chỉ để test trong Editor
    }
}
