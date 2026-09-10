using ShootingGallery.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShootingGallery.UI
{
    /// <summary>Host/Join-by-IP menu. Talks only to ConnectionManager, never to UnityTransport directly.</summary>
    public class MainMenuUI : MonoBehaviour
    {
        [SerializeField] private InputField joinAddressField;
        [SerializeField] private Button hostButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Text statusText;
        [SerializeField] private string arenaSceneName = "Arena";

        private void Awake()
        {
            hostButton.onClick.AddListener(OnHostClicked);
            joinButton.onClick.AddListener(OnJoinClicked);
        }

        private void OnHostClicked()
        {
            SetStatus("Starting host...");
            bool started = ConnectionManager.Instance.StartHost();
            if (started)
            {
                SetStatus("Hosting - loading arena...");
                NetworkManager.Singleton.SceneManager.LoadScene(arenaSceneName, LoadSceneMode.Single);
            }
            else
            {
                SetStatus("Failed to start host.");
            }
        }

        private void OnJoinClicked()
        {
            string address = string.IsNullOrWhiteSpace(joinAddressField.text) ? "127.0.0.1" : joinAddressField.text.Trim();
            SetStatus($"Connecting to {address}...");
            bool started = ConnectionManager.Instance.StartClient(address);
            SetStatus(started ? "Connecting - waiting for host..." : "Failed to start client.");
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }
        }
    }
}
