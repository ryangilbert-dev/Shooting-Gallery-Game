using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShootingGallery.Networking
{
    /// <summary>
    /// Lives only in Bootstrap.unity (the scene the game actually launches into). Immediately
    /// hands off to the main menu; the NetworkManager and ConnectionManager objects in this
    /// scene are DontDestroyOnLoad and survive the switch.
    /// </summary>
    public class BootstrapLoader : MonoBehaviour
    {
        [SerializeField] private string mainMenuSceneName = "MainMenu";

        private void Start()
        {
            SceneManager.LoadScene(mainMenuSceneName, LoadSceneMode.Single);
        }
    }
}
