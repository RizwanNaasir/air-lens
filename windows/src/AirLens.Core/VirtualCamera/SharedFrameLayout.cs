namespace AirLens.Core.VirtualCamera;

/// <summary>
/// Byte offsets of the shared-memory contract in
/// windows/src/AirLens.VirtualCamera/include/AirLensSharedFrame.h. Change both together; the C++
/// header has static_asserts pinning the same numbers.
/// </summary>
public static class SharedFrameLayout
{
    public const string MappingName = @"Global\AirLensVirtualCamFrames";
    public const string EventName = @"Global\AirLensVirtualCamFrameReady";
    public const uint Magic = 0x43564C41; // 'ALVC'
    public const uint Version = 1;
    public const int SlotCount = 3;
    public const int MaxWidth = 3840;
    public const int MaxHeight = 2160;
    public const int HeaderSize = 4096;
    public const uint FormatNv12 = 1;
    public const int SlotStride = ((MaxWidth * MaxHeight * 3 / 2) + 4095) & ~4095;
    public const long MappingSize = HeaderSize + (long)SlotCount * SlotStride;

    // AirLensVCamHeader
    public const int OffMagic = 0;
    public const int OffVersion = 4;
    public const int OffConsumerHeartbeat = 32;
    public const int OffRequestedWidth = 40;
    public const int OffRequestedHeight = 44;
    public const int OffProducerWidth = 48;
    public const int OffProducerHeight = 52;
    public const int OffProducerFpsNum = 56;
    public const int OffProducerFpsDen = 60;
    public const int OffWriteSequence = 64;
    public const int OffSlots = 128;
    public const int SlotHeaderSize = 64;

    // AirLensVCamSlot (relative to the slot header)
    public const int SlotOffSequence = 0;
    public const int SlotOffTimestamp = 8;
    public const int SlotOffWidth = 16;
    public const int SlotOffHeight = 20;
    public const int SlotOffStrideY = 24;
    public const int SlotOffStrideUV = 28;
    public const int SlotOffFormat = 32;
    public const int SlotOffFlags = 36;

    public static long SlotDataOffset(int slot) => HeaderSize + (long)slot * SlotStride;
}
