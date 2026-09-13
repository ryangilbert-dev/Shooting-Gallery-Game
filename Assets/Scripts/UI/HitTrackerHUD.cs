using ShootingGallery.Gameplay;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace ShootingGallery.UI
{
    /// <summary>
    /// Owner-only HUD bar showing how close the local player is to filling their own gallery
    /// lane's hit tracker (see MatchManager.RegisterTargetHit): each landed shot fills it by
    /// 1/HitsPerBarFill, and filling it drops the divider wall for a few seconds before it
    /// resets to empty. Built entirely from code at runtime - no Canvas/prefab wiring needed -
    /// matching how the weapon's tracer material is also created purely in script.
    /// </summary>
    public class HitTrackerHUD : NetworkBehaviour
    {
        [SerializeField] private Color barBackgroundColor = new Color(0f, 0f, 0f, 0.5f);
        [SerializeField] private Color barFillColor = new Color(0.85f, 0.65f, 0.1f);
        [SerializeField] private Vector2 barSize = new Vector2(320f, 26f);
        [SerializeField] private float barBottomMargin = 40f;

        private Image fillImage;
        private LaneSide subscribedLane = LaneSide.None;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner)
            {
                // A remote client watching another player has nothing to poll for below -
                // disable outright instead of letting Update() check IsOwner every frame forever.
                enabled = false;
                return;
            }

            BuildBar();
        }

        public override void OnNetworkDespawn()
        {
            Unsubscribe();
        }

        private void Update()
        {
            // MatchManager might not exist yet at spawn time - same timing quirk PlayerController
            // works around by polling. Keep checking until it's up, then latch onto whichever
            // lane this client actually gets assigned. This component is owner-only by this point
            // (see OnNetworkSpawn), so no IsOwner check needed here.
            if (subscribedLane == LaneSide.None && MatchManager.Instance != null)
            {
                Subscribe();
            }
        }

        private void Subscribe()
        {
            LaneSide lane = MatchManager.Instance.GetLaneForClient(OwnerClientId);
            if (lane == LaneSide.None)
            {
                return; // Not assigned a lane yet - will retry next Update.
            }

            subscribedLane = lane;
            NetworkVariable<int> hitCount = lane == LaneSide.A
                ? MatchManager.Instance.PlayerAHitCount
                : MatchManager.Instance.PlayerBHitCount;
            hitCount.OnValueChanged += HandleHitCountChanged;
            ApplyFill(hitCount.Value);

            // From here on the bar updates entirely off the OnValueChanged subscription above -
            // nothing left for Update() to poll for, so stop being called every frame.
            enabled = false;
        }

        private void Unsubscribe()
        {
            if (subscribedLane == LaneSide.None || MatchManager.Instance == null)
            {
                return;
            }

            NetworkVariable<int> hitCount = subscribedLane == LaneSide.A
                ? MatchManager.Instance.PlayerAHitCount
                : MatchManager.Instance.PlayerBHitCount;
            hitCount.OnValueChanged -= HandleHitCountChanged;
        }

        private void HandleHitCountChanged(int previous, int current)
        {
            ApplyFill(current);
        }

        private void ApplyFill(int hitCount)
        {
            if (fillImage == null || MatchManager.Instance == null)
            {
                return;
            }

            fillImage.fillAmount = (float)hitCount / MatchManager.Instance.HitsPerBarFill;
        }

        private void BuildBar()
        {
            var canvasGO = new GameObject("HitTrackerCanvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Default CanvasScaler mode (Constant Pixel Size) renders at a literal pixel size
            // regardless of screen resolution - looks fine in the Editor's small Game view panel,
            // comically tiny at a real build's full native resolution. Scale With Screen Size
            // instead, relative to a 1920x1080 reference so this bar's existing pixel sizing
            // stays meaningful.
            var canvasScaler = canvasGO.AddComponent<CanvasScaler>();
            canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasScaler.matchWidthOrHeight = 0.5f;

            var backgroundGO = new GameObject("HitBarBackground", typeof(RectTransform));
            backgroundGO.transform.SetParent(canvasGO.transform, false);
            var backgroundRect = (RectTransform)backgroundGO.transform;
            backgroundRect.anchorMin = new Vector2(0.5f, 0f);
            backgroundRect.anchorMax = new Vector2(0.5f, 0f);
            backgroundRect.pivot = new Vector2(0.5f, 0f);
            backgroundRect.anchoredPosition = new Vector2(0f, barBottomMargin);
            backgroundRect.sizeDelta = barSize;
            var backgroundImage = backgroundGO.AddComponent<Image>();
            backgroundImage.sprite = GetWhiteSprite();
            backgroundImage.color = barBackgroundColor;

            var fillGO = new GameObject("HitBarFill", typeof(RectTransform));
            fillGO.transform.SetParent(backgroundGO.transform, false);
            var fillRect = (RectTransform)fillGO.transform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(3f, 3f);
            fillRect.offsetMax = new Vector2(-3f, -3f);
            fillImage = fillGO.AddComponent<Image>();
            fillImage.sprite = GetWhiteSprite();
            fillImage.color = barFillColor;
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImage.fillAmount = 0f;
        }

        // uGUI's Image needs an actual Sprite (not just a texture) to render at all - Texture2D's
        // built-in 4x4 white texture is always available at runtime, so wrapping it in a Sprite
        // gives a plain white fillable bar without needing any imported sprite asset.
        private static Sprite GetWhiteSprite()
        {
            Texture2D texture = Texture2D.whiteTexture;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }
    }
}
