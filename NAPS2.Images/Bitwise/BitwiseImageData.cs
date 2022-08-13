namespace NAPS2.Images.Bitwise;

public struct BitwiseImageData
{
    public unsafe BitwiseImageData(byte* ptr, PixelInfo pix)
    {
        this.ptr = ptr;
        stride = pix.Stride;
        w = pix.Width;
        h = pix.Height;
        var sub = pix.SubPixelType;
        bitsPerPixel = sub.BitsPerPixel;
        bytesPerPixel = sub.BytesPerPixel;
        rOff = sub.RedOffset;
        gOff = sub.GreenOffset;
        bOff = sub.BlueOffset;
        aOff = sub.AlphaOffset;
        invertY = pix.InvertY;
    }

    public unsafe byte* ptr;
    public int stride;
    public int w;
    public int h;
    public int bitsPerPixel;
    public int bytesPerPixel;
    public int rOff;
    public int gOff;
    public int bOff;
    public int aOff;
    public bool invertY;

    public (int, int, int, int, int) BitLayout => (bitsPerPixel, rOff, gOff, bOff, aOff);
}