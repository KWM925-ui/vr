using UnityEngine;

namespace DragonBoatVR
{
    // A value-only observation from the same completed physics step as the water
    // effects. Audio does not clone a full protocol/recording frame at 100 Hz.
    public struct RiverAudioMotionSample
    {
        public double timeSeconds;
        public float stepSeconds, wetFraction, paddleRelativeSpeed, hullRelativeSpeed, paddleLoadN;
        public PaddleSample paddle;
        public Vector3 waterContact, boatPosition, boatVelocity, boatAngularVelocity, boatLocalAcceleration;
        public Quaternion boatRotation;
    }
}
