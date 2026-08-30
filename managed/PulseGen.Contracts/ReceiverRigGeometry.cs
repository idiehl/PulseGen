namespace PulseGen.Contracts;

public static class ReceiverRigGeometry
{
    public static (MicrophoneCapsule Left, MicrophoneCapsule Right) EffectiveMicrophoneCapsules(ReceiverRig receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (receiver.Kind != ReceiverRigKind.StereoMicrophone)
        {
            throw new ArgumentException("Only stereo microphone rigs have microphone capsules.", nameof(receiver));
        }

        var halfSpacing = receiver.SpacingMetres * 0.5;
        var halfAngle = receiver.AngleDegrees * 0.5;
        var left = receiver.LeftCapsule ?? new MicrophoneCapsule
        {
            RelativeTransform = new Transform
            {
                PositionMetres = new Vector3d(-halfSpacing, 0, 0),
                RotationDegrees = new Vector3d(0, -halfAngle, 0)
            },
            PolarPattern = receiver.PolarPattern,
            SensitivityDb = receiver.SensitivityDb,
            SelfNoiseDbSpl = receiver.SelfNoiseDbSpl
        };
        var right = receiver.RightCapsule ?? new MicrophoneCapsule
        {
            RelativeTransform = new Transform
            {
                PositionMetres = new Vector3d(halfSpacing, 0, 0),
                RotationDegrees = new Vector3d(0, halfAngle, 0)
            },
            PolarPattern = receiver.PolarPattern,
            SensitivityDb = receiver.SensitivityDb,
            SelfNoiseDbSpl = receiver.SelfNoiseDbSpl
        };
        return (left, right);
    }

    public static Vector3d CapsuleWorldPosition(
        ReceiverRig receiver,
        MicrophoneCapsule capsule,
        Vector3d receiverCentre)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(capsule);
        return receiverCentre + RotateYaw(capsule.RelativeTransform.PositionMetres, receiver.Transform.RotationDegrees.Y);
    }

    public static Vector3d RotateYaw(Vector3d value, double yawDegrees)
    {
        var radians = yawDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new Vector3d(
            (value.X * cosine) + (value.Z * sine),
            value.Y,
            (-value.X * sine) + (value.Z * cosine));
    }
}
