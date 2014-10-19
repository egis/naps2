﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.SqlServer.Server;
using NAPS2.Lang.Resources;
using NAPS2.Scan;
using NAPS2.Scan.Images;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace NAPS2.ImportExport.Pdf
{
    public class PdfSharpImporter : IPdfImporter
    {
        private readonly IScannedImageFactory scannedImageFactory;
        private readonly IErrorOutput errorOutput;

        public PdfSharpImporter(IScannedImageFactory scannedImageFactory, IErrorOutput errorOutput)
        {
            this.scannedImageFactory = scannedImageFactory;
            this.errorOutput = errorOutput;
        }

        public IEnumerable<IScannedImage> Import(string filePath)
        {
            PdfDocument document = PdfReader.Open(filePath);
            if (document.Info.Creator != MiscResources.NAPS2 && document.Info.Author != MiscResources.NAPS2)
            {
                errorOutput.DisplayError(string.Format(MiscResources.ImportErrorNAPS2Pdf, Path.GetFileName(filePath)));
                return Enumerable.Empty<IScannedImage>();
            }

            try
            {
                return document.Pages.Cast<PdfPage>().SelectMany(GetImagesFromPage).ToList();
            }
            catch (Exception e)
            {
                errorOutput.DisplayError(string.Format(MiscResources.ImportErrorCouldNot, Path.GetFileName(filePath)));
                Log.ErrorException("Error importing PDF file.", e);
                return Enumerable.Empty<IScannedImage>();
            }
        }

        private IEnumerable<IScannedImage> GetImagesFromPage(PdfPage page)
        {
            // Get resources dictionary
            PdfDictionary resources = page.Elements.GetDictionary("/Resources");
            if (resources == null)
            {
                yield break;
            }
            // Get external objects dictionary
            PdfDictionary xObjects = resources.Elements.GetDictionary("/XObject");
            if (xObjects == null)
            {
                yield break;
            }
            // Iterate references to external objects
            foreach (PdfItem item in xObjects.Elements.Values)
            {
                var reference = item as PdfReference;
                if (reference == null)
                {
                    continue;
                }
                var xObject = reference.Value as PdfDictionary;
                // Is external object an image?
                if (xObject != null && xObject.Elements.GetString("/Subtype") == "/Image")
                {
                    switch (xObject.Elements.GetName("/Filter"))
                    {
                        case "/DCTDecode":
                            yield return ExportJpegImage(xObject);
                            break;

                        case "/FlateDecode":
                            yield return ExportAsPngImage(xObject);
                            break;

                        default:
                            throw new NotImplementedException("Unsupported image encoding");
                    }
                }
            }
        }

        private IScannedImage ExportJpegImage(PdfDictionary image)
        {
            // Fortunately JPEG has native support in PDF and exporting an image is just writing the stream to a file.
            using (var memoryStream = new MemoryStream(image.Stream.Value))
            {
                var bitmap = new Bitmap(memoryStream);
                return scannedImageFactory.Create(bitmap, ScanBitDepth.C24Bit, false);
            }
        }

        private IScannedImage ExportAsPngImage(PdfDictionary image)
        {
            int width = image.Elements.GetInteger(PdfImage.Keys.Width);
            int height = image.Elements.GetInteger(PdfImage.Keys.Height);
            int bitsPerComponent = image.Elements.GetInteger(PdfImage.Keys.BitsPerComponent);

            var buffer = image.Stream.UnfilteredValue;

            Bitmap bitmap;
            ScanBitDepth bitDepth;
            switch (bitsPerComponent)
            {
                case 8:
                    bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    bitDepth = ScanBitDepth.C24Bit;
                    RgbToBitmapUnmanaged(height, width, bitmap, buffer);
                    break;
                case 1:
                    bitmap = new Bitmap(width, height, PixelFormat.Format1bppIndexed);
                    bitDepth = ScanBitDepth.BlackWhite;
                    BlackAndWhiteToBitmapUnmanaged(height, width, bitmap, buffer);
                    break;
                default:
                    throw new NotImplementedException("Unsupported image encoding (expected 24 bpp or 1bpp)");
            }

            return scannedImageFactory.Create(bitmap, bitDepth, true);
        }

        private static void RgbToBitmapUnmanaged(int height, int width, Bitmap bitmap, byte[] rgbBuffer)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        IntPtr pixelData = data.Scan0 + y * data.Stride + x * 3;
                        int bufferIndex = (y * width + x) * 3;
                        Marshal.WriteByte(pixelData, rgbBuffer[bufferIndex + 2]);
                        Marshal.WriteByte(pixelData + 1, rgbBuffer[bufferIndex + 1]);
                        Marshal.WriteByte(pixelData + 2, rgbBuffer[bufferIndex]);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static void BlackAndWhiteToBitmapUnmanaged(int height, int width, Bitmap bitmap, byte[] bwBuffer)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
            try
            {
                int bytesPerRow = (width - 1) / 8 + 1;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < bytesPerRow; x++)
                    {
                        IntPtr pixelData = data.Scan0 + y * data.Stride + x;
                        Marshal.WriteByte(pixelData, bwBuffer[y * bytesPerRow + x]);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
    }
}
