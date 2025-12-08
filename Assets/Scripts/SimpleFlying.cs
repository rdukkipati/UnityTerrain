using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class SimpleFlying : MonoBehaviour
{
    public float moveSpeed = 50f;        // Horizontal movement speed
    public float lookSensitivity = 8f;   // Mouse look sensitivity
    public float gravity = 9.81f;        // Gravity strength

    private CharacterController controller;
    private Vector3 velocity = Vector3.zero;
    private float pitch = 0f;
    private float yaw = 0f;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        transform.position = new Vector3(0, 8024, 0);
        transform.rotation = Quaternion.identity;
    }

    void Update()
    {
        // === Mouse Look ===
        float mouseX = Input.GetAxis("Mouse X") * lookSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * lookSensitivity;

        yaw += mouseX;
        pitch -= mouseY;
        pitch = Mathf.Clamp(pitch, -89f, 89f);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        // === Movement Input ===
        Vector3 move = Vector3.zero;
        Vector3 forward = transform.forward;
        Vector3 right = transform.right;
        forward.y = 0;
        right.y = 0;

        if (Input.GetKey(KeyCode.W)) move += forward;
        if (Input.GetKey(KeyCode.S)) move -= forward;
        if (Input.GetKey(KeyCode.A)) move -= right;
        if (Input.GetKey(KeyCode.D)) move += right;

        // Vertical input (optional flying)
        if (Input.GetKey(KeyCode.Space)) move += Vector3.up;
        if (Input.GetKey(KeyCode.LeftShift)) move += Vector3.down;

        move = move.normalized * moveSpeed;

        // === Gravity ===
        if (!controller.isGrounded)
        {
            velocity += Vector3.down * gravity * Time.deltaTime;
        }
        else
        {
            // Reset downward velocity when on the ground
            velocity.y = 0f;
        }

        // Combine horizontal and vertical movement
        Vector3 finalMove = move + new Vector3(0, velocity.y, 0);

        // Move the character
        controller.Move(finalMove * Time.deltaTime);
    }
}