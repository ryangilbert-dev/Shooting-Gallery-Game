using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Simple owner-driven FPS movement: WASD to walk, mouse to look. The player's
    /// NetworkTransform is set to Owner authority (see PlayerMovementSetup), so moving the local
    /// transform here is all that's needed - it replicates to everyone else automatically.
    /// Not server-validated yet; fine for free exploration of the bar/gallery rooms, revisit if
    /// anti-cheat becomes a concern once this movement is also usable during live match phases.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMovement : NetworkBehaviour
    {
        [SerializeField] private Transform cameraPivot;
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float mouseSensitivity = 0.15f;
        [SerializeField] private float gravity = -18f;
        [SerializeField] private float pitchClamp = 85f;

        private CharacterController controller;
        private float pitch;
        private float verticalVelocity;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            if (!IsOwner)
            {
                return;
            }

            HandleCursorToggle();
            HandleLook();
            HandleMove();
        }

        private void HandleCursorToggle()
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                bool isLocked = Cursor.lockState == CursorLockMode.Locked;
                Cursor.lockState = isLocked ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = isLocked;
            }
        }

        private void HandleLook()
        {
            if (Mouse.current == null || Cursor.lockState != CursorLockMode.Locked)
            {
                return;
            }

            Vector2 delta = Mouse.current.delta.ReadValue() * mouseSensitivity;
            transform.Rotate(Vector3.up, delta.x, Space.World);

            pitch = Mathf.Clamp(pitch - delta.y, -pitchClamp, pitchClamp);
            if (cameraPivot != null)
            {
                cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }
        }

        private void HandleMove()
        {
            if (Keyboard.current == null)
            {
                return;
            }

            Vector2 input = Vector2.zero;
            if (Keyboard.current.wKey.isPressed) input.y += 1f;
            if (Keyboard.current.sKey.isPressed) input.y -= 1f;
            if (Keyboard.current.aKey.isPressed) input.x -= 1f;
            if (Keyboard.current.dKey.isPressed) input.x += 1f;
            input = Vector2.ClampMagnitude(input, 1f);

            Vector3 move = (transform.right * input.x + transform.forward * input.y) * moveSpeed;

            if (controller.isGrounded && verticalVelocity < 0f)
            {
                verticalVelocity = -1f;
            }
            verticalVelocity += gravity * Time.deltaTime;
            move.y = verticalVelocity;

            controller.Move(move * Time.deltaTime);
        }
    }
}
