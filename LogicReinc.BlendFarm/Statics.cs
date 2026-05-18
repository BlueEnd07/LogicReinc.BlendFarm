using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LogicReinc.BlendFarm
{
    public static class Statics
    {
        private static object _previewImageLock = new object();
        private static Avalonia.Media.Imaging.Bitmap _noPreviewImage { get; set; }
        public static Avalonia.Media.Imaging.Bitmap NoPreviewImage
        {
            get
            {
                lock (_previewImageLock)
                {
                    if (_noPreviewImage == null)
                    {
                        System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(700, 200);
                        using(System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.DrawString("Could not generate Preview\n(Some Render Formats do not support preview)", 
                                new System.Drawing.Font("Arial", 16), 
                                new System.Drawing.SolidBrush(System.Drawing.Color.Gray),
                                5,5);
                        }
                        _noPreviewImage = bmp.ToAvaloniaBitmap();
                    }
                }
                return _noPreviewImage;
            }
        }

        public static string SanitizePath(string inputPath)
        {
            if (inputPath == null)
                return inputPath;

            //Fix Linux Space escape
            inputPath = inputPath.Replace("\\040", " ");


            return inputPath;
        }

        public static string FormatAnimationFrameFileName(string frameFormat, int frame, int startFrame, int endFrame)
        {
            if (string.IsNullOrEmpty(frameFormat))
                return frame.ToString();

            Match match = Regex.Match(frameFormat, "#+");
            if (!match.Success)
                return frameFormat;

            int widestFrame = Math.Max(Math.Abs(startFrame), Math.Abs(endFrame));
            int width = match.Length > 1 ? match.Length : Math.Max(3, widestFrame.ToString().Length);
            string frameText = frame.ToString().PadLeft(width, '0');

            return frameFormat.Substring(0, match.Index) + frameText + frameFormat.Substring(match.Index + match.Length);
        }

        public static Bitmap ToAvaloniaBitmap(this System.Drawing.Image bitmap)
        {
            //TODO: This needs to be better..
            using (MemoryStream str = new MemoryStream())
            {
                bitmap.Save(str, ImageFormat.Png);
                str.Position = 0;
                return new Bitmap(str);
            }
        }
    }
}
