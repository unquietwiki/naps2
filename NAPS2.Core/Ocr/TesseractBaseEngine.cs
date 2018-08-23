﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using NAPS2.Config;
using NAPS2.Dependencies;
using NAPS2.Util;

namespace NAPS2.Ocr
{
    public abstract class TesseractBaseEngine : IOcrEngine
    {
        private const int DEFAULT_TIMEOUT = 120 * 1000;
        private const int CHECK_INTERVAL = 500;
        
        private readonly AppConfigManager appConfigManager;

        protected TesseractBaseEngine(AppConfigManager appConfigManager)
        {
            this.appConfigManager = appConfigManager;
        }

        public bool CanProcess(string langCode)
        {
            if (string.IsNullOrEmpty(langCode) || !IsInstalled || !IsSupported)
            {
                return false;
            }
            // Support multiple specified languages (e.g. "eng+fra")
            return langCode.Split('+').All(code => InstalledLanguages.Any(x => x.Code == code));
        }

        public OcrResult ProcessImage(string imagePath, OcrParams ocrParams, Func<bool> cancelCallback)
        {
            string tempHocrFilePath = Path.Combine(Paths.Temp, Path.GetRandomFileName());
            string tempHocrFilePathWithExt = tempHocrFilePath + TesseractHocrExtension;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = TesseractExePath,
                    Arguments = $"\"{imagePath}\" \"{tempHocrFilePath}\" -l {ocrParams.LanguageCode} hocr",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.EnvironmentVariables["TESSDATA_PREFIX"] = TesseractPrefixPath;
                var tessdata = new DirectoryInfo(Path.Combine(TesseractDataPath, "tessdata"));
                EnsureHocrConfigExists(tessdata);
                var tesseractProcess = Process.Start(startInfo);
                if (tesseractProcess == null)
                {
                    // Couldn't start tesseract for some reason
                    Log.Error("Couldn't start OCR process.");
                    return null;
                }
                var timeout = (int)(appConfigManager.Config.OcrTimeoutInSeconds * 1000);
                if (timeout == 0)
                {
                    timeout = DEFAULT_TIMEOUT;
                }
                var stopwatch = Stopwatch.StartNew();
                while (!tesseractProcess.WaitForExit(CHECK_INTERVAL))
                {
                    if (stopwatch.ElapsedMilliseconds >= timeout || cancelCallback())
                    {
                        if (stopwatch.ElapsedMilliseconds >= timeout)
                        {
                            Log.Error("OCR process timed out.");
                        }
                        try
                        {
                            tesseractProcess.Kill();
                            // Wait a bit to give the process time to release its file handles
                            Thread.Sleep(200);
                        }
                        catch (Exception e)
                        {
                            Log.ErrorException("Error killing OCR process", e);
                        }
                        return null;
                    }
                }
#if DEBUG
                var output = tesseractProcess.StandardOutput.ReadToEnd();
                if (output.Length > 0)
                {
                    Log.Error("Tesseract stdout: {0}", output);
                }
                output = tesseractProcess.StandardError.ReadToEnd();
                if (output.Length > 0)
                {
                    Log.Error("Tesseract stderr: {0}", output);
                }
#endif
                XDocument hocrDocument = XDocument.Load(tempHocrFilePathWithExt);
                return new OcrResult
                {
                    PageBounds = hocrDocument.Descendants()
                        .Where(x => x.Attributes("class").Any(y => y.Value == "ocr_page"))
                        .Select(x => GetBounds(x.Attribute("title")))
                        .First(),
                    Elements = hocrDocument.Descendants()
                        .Where(x => x.Attributes("class").Any(y => y.Value == "ocrx_word"))
                        .Select(x => new OcrResultElement { Text = x.Value, Bounds = GetBounds(x.Attribute("title")) }),
                    RightToLeft = InstalledLanguages.Where(x => x.Code == ocrParams.LanguageCode).Select(x => x.RTL).FirstOrDefault()
                };
            }
            catch (Exception e)
            {
                Log.ErrorException("Error running OCR", e);
                return null;
            }
            finally
            {
                try
                {
                    File.Delete(tempHocrFilePathWithExt);
                }
                catch (Exception e)
                {
                    Log.ErrorException("Error cleaning up OCR temp files", e);
                }
            }
        }

        private void EnsureHocrConfigExists(DirectoryInfo tessdata)
        {
            var configDir = new DirectoryInfo(Path.Combine(tessdata.FullName, "configs"));
            if (!configDir.Exists)
            {
                configDir.Create();
            }
            var hocrConfigFile = new FileInfo(Path.Combine(configDir.FullName, "hocr"));
            if (!hocrConfigFile.Exists)
            {
                using (var writer = hocrConfigFile.CreateText())
                {
                    writer.Write("tessedit_create_hocr 1");
                }
            }
        }

        private Rectangle GetBounds(XAttribute titleAttr)
        {
            var bounds = new Rectangle();
            if (titleAttr != null)
            {
                foreach (var param in titleAttr.Value.Split(';'))
                {
                    string[] parts = param.Trim().Split(' ');
                    if (parts.Length == 5 && parts[0] == "bbox")
                    {
                        int x1 = int.Parse(parts[1]), y1 = int.Parse(parts[2]);
                        int x2 = int.Parse(parts[3]), y2 = int.Parse(parts[4]);
                        bounds = new Rectangle(x1, y1, x2 - x1, y2 - y1);
                    }
                }
            }
            return bounds;
        }

        protected abstract string TesseractExePath { get; }

        protected abstract string TesseractHocrExtension { get; }

        protected abstract string TesseractDataPath { get; }

        protected abstract string TesseractPrefixPath { get; }

        public virtual bool IsSupported => PlatformSupport.Validate();

        protected abstract PlatformSupport PlatformSupport { get; }

        public abstract bool IsInstalled { get; }

        public abstract bool IsUpgradable { get; }

        public abstract bool CanInstall { get; }

        public abstract IEnumerable<Language> InstalledLanguages { get; }

        public abstract ExternalComponent Component { get; }

        public abstract IEnumerable<ExternalComponent> LanguageComponents { get; }

        public abstract IEnumerable<OcrMode> SupportedModes { get; }

        protected class TesseractLanguage
        {
            public string Filename { get; set; }

            public string Code { get; set; }

            public string LangName { get; set; }

            public double Size { get; set; }

            public string Sha1 { get; set; }

            public bool RTL { get; set; }
        }

        protected readonly IDictionary<string, Language> Languages = LanguageData.ToDictionary(x => x.Code, x => new Language(x.Code, x.LangName, x.RTL));

        #region Language Data (auto-generated)

        protected static readonly TesseractLanguage[] LanguageData =
        {
            new TesseractLanguage { Filename = "afr.traineddata.gz", Code = "afr", LangName = "Afrikaans", Size = 1.93, Sha1 = "a669186130bf1fc6c78226ac868c82b70a44c70b" },
            new TesseractLanguage { Filename = "amh.traineddata.gz", Code = "amh", LangName = "Amharic", Size = 1.03, Sha1 = "1153cbbac7306d42e72ca639ff3f36f45dcb15a2" },
            new TesseractLanguage { Filename = "ara.traineddata.gz", Code = "ara", LangName = "Arabic", Size = 1.62, Sha1 = "87b76c73fdcc4c54ec1f03d83b6df665430c2b06", RTL = true },
            new TesseractLanguage { Filename = "asm.traineddata.gz", Code = "asm", LangName = "Assamese", Size = 6.56, Sha1 = "223900790d10f638b7dca2a8b8e8a15295d1f19c" },
            new TesseractLanguage { Filename = "aze.traineddata.gz", Code = "aze", LangName = "Azerbaijani", Size = 2.54, Sha1 = "01607e49fe6ba6604f65d9b57c77b403ab74040a" },
            new TesseractLanguage { Filename = "aze_cyrl.traineddata.gz", Code = "aze_cyrl", LangName = "Azerbaijani (Cyrillic)", Size = 0.97, Sha1 = "f9c9b153e8825bb92d9c8005342ac3d5ea81d0bc" },
            new TesseractLanguage { Filename = "bel.traineddata.gz", Code = "bel", LangName = "Belarusian", Size = 2.43, Sha1 = "3ac0935dd22f4f2730286d5cb127324d27718410" },
            new TesseractLanguage { Filename = "ben.traineddata.gz", Code = "ben", LangName = "Bengali", Size = 6.45, Sha1 = "479674b283db6e84fdfb17386056f2e9a5b41b9c" },
            new TesseractLanguage { Filename = "bod.traineddata.gz", Code = "bod", LangName = "Tibetan", Size = 10.74, Sha1 = "3ff199544dc9e7994658231cbc999878e23463db" },
            new TesseractLanguage { Filename = "bos.traineddata.gz", Code = "bos", LangName = "Bosnian", Size = 1.87, Sha1 = "9d0bb89c53251789bba06de1452cf1a74d978f35" },
            new TesseractLanguage { Filename = "bul.traineddata.gz", Code = "bul", LangName = "Bulgarian", Size = 2.20, Sha1 = "ac0481cc1fe62c3af5a34d57fa1571dfd2a95865" },
            new TesseractLanguage { Filename = "cat.traineddata.gz", Code = "cat", LangName = "Catalan", Size = 1.97, Sha1 = "e1e1dc2e37f6b085bdefdb9d0d63d3ad086ef1f4" },
            new TesseractLanguage { Filename = "ceb.traineddata.gz", Code = "ceb", LangName = "Cebuano", Size = 0.58, Sha1 = "f867102f828b6495996370eea6ed8688af219b17" },
            new TesseractLanguage { Filename = "ces.traineddata.gz", Code = "ces", LangName = "Czech", Size = 4.65, Sha1 = "155f60a0994f1590d3d3ba29ec1a5bca3f16efdd" },
            new TesseractLanguage { Filename = "chi_sim.traineddata.gz", Code = "chi_sim", LangName = "Chinese (Simplified)", Size = 17.60, Sha1 = "9bd65dcecd2581e8f588cec11cd1e2f754885fcb" },
            new TesseractLanguage { Filename = "chi_tra.traineddata.gz", Code = "chi_tra", LangName = "Chinese (Traditional)", Size = 24.11, Sha1 = "5abef9af8a4fd83a0d156ee2e1d5234c80bb836b" },
            new TesseractLanguage { Filename = "chr.traineddata.gz", Code = "chr", LangName = "Cherokee", Size = 0.36, Sha1 = "d3677cb6c57ec1b14625a5594dad159a1ad9ec93" },
            new TesseractLanguage { Filename = "cym.traineddata.gz", Code = "cym", LangName = "Welsh", Size = 1.36, Sha1 = "a5d5733d45710f6da1c4b19f0903bf5edb10a484" },
            new TesseractLanguage { Filename = "dan.traineddata.gz", Code = "dan", LangName = "Danish", Size = 2.76, Sha1 = "eb813b0c299261b9535a2c684e51f159f05ae8ea" },
            new TesseractLanguage { Filename = "dan_frak.traineddata.gz", Code = "dan_frak", LangName = "Danish (Fraktur)", Size = 0.65, Sha1 = "dcb540024688da096399e52ff9826aad1d71479c" },
            new TesseractLanguage { Filename = "deu.traineddata.gz", Code = "deu", LangName = "German", Size = 5.48, Sha1 = "f575f3fcb554077b906aaaac8850d5bd56967cbd" },
            new TesseractLanguage { Filename = "deu_frak.traineddata.gz", Code = "deu_frak", LangName = "German (Fraktur)", Size = 0.78, Sha1 = "28ac257129f881b3a09c099004048bf6de4bc952" },
            new TesseractLanguage { Filename = "dzo.traineddata.gz", Code = "dzo", LangName = "Dzongkha", Size = 1.32, Sha1 = "6eb0c943242e4d906cbebec2cf43b2ca63979424" },
            new TesseractLanguage { Filename = "ell.traineddata.gz", Code = "ell", LangName = "Greek", Size = 2.00, Sha1 = "e54ab7455c1d4715652253321f693e221b61ac8b" },
            new TesseractLanguage { Filename = "eng.traineddata.gz", Code = "eng", LangName = "English", Size = 9.02, Sha1 = "36bfd5953540b3c294c62402e303f381cee156f3" },
            new TesseractLanguage { Filename = "enm.traineddata.gz", Code = "enm", LangName = "Middle English (1100-1500)", Size = 0.77, Sha1 = "02486b802f4f83b5d9198309955cbf4aa38e5e05" },
            new TesseractLanguage { Filename = "epo.traineddata.gz", Code = "epo", LangName = "Esperanto", Size = 2.42, Sha1 = "465dfb934eb45116ebe7f3c4e3adf28826e49dca" },
            new TesseractLanguage { Filename = "equ.traineddata.gz", Code = "equ", LangName = "Math / equation detection", Size = 0.78, Sha1 = "c9bc582875cf7c7903b529a9cdb0b9f4669b840d" },
            new TesseractLanguage { Filename = "est.traineddata.gz", Code = "est", LangName = "Estonian", Size = 3.62, Sha1 = "d743f2456fa32ce7bbbb80cb40951eb742692596" },
            new TesseractLanguage { Filename = "eus.traineddata.gz", Code = "eus", LangName = "Basque", Size = 1.83, Sha1 = "d991552b861e5ea1dca59ffca7e295b323e62bbf" },
            new TesseractLanguage { Filename = "fas.traineddata.gz", Code = "fas", LangName = "Persian", Size = 1.75, Sha1 = "c8a7a6b11c3f455b07a397af2e51705a68ff5f77", RTL = true },
            new TesseractLanguage { Filename = "fin.traineddata.gz", Code = "fin", LangName = "Finnish", Size = 4.98, Sha1 = "90232ad3572901a35bd4bbc736d47184171fa0fd" },
            new TesseractLanguage { Filename = "fra.traineddata.gz", Code = "fra", LangName = "French", Size = 5.65, Sha1 = "2bebc5a4c981443c1cbff254e0ca3120004a6c7b" },
            new TesseractLanguage { Filename = "frk.traineddata.gz", Code = "frk", LangName = "Frankish", Size = 6.64, Sha1 = "1a6984f8b5768ae663f293ea04594fca229bdb16" },
            new TesseractLanguage { Filename = "frm.traineddata.gz", Code = "frm", LangName = "Middle French (ca. 1400-1600)", Size = 6.34, Sha1 = "64e0c6e00352833b206f8b26b6410d0d544b798d" },
            new TesseractLanguage { Filename = "gle.traineddata.gz", Code = "gle", LangName = "Irish", Size = 1.25, Sha1 = "994c111e9c24e74bf7105f42a3e39d87ea24f258" },
            new TesseractLanguage { Filename = "glg.traineddata.gz", Code = "glg", LangName = "Galician", Size = 2.04, Sha1 = "201c627e518099c15dbbecd72e6e4782e389f619" },
            new TesseractLanguage { Filename = "grc.traineddata.gz", Code = "grc", LangName = "Ancient Greek", Size = 1.88, Sha1 = "ae58a943620c485d33ba95b3fcaca79314105d56" },
            new TesseractLanguage { Filename = "guj.traineddata.gz", Code = "guj", LangName = "Gujarati", Size = 4.39, Sha1 = "f469d7257f39dcdd0668d768886f19084816b10e" },
            new TesseractLanguage { Filename = "hat.traineddata.gz", Code = "hat", LangName = "Haitian", Size = 0.49, Sha1 = "1667e25ebfe6dc74695af413f291e20f1eec552a" },
            new TesseractLanguage { Filename = "heb.traineddata.gz", Code = "heb", LangName = "Hebrew", Size = 1.51, Sha1 = "64401c999ef08d6190a11a4347c8f9acf40a8e50", RTL = true },
            new TesseractLanguage { Filename = "hin.traineddata.gz", Code = "hin", LangName = "Hindi", Size = 6.28, Sha1 = "dae6a9a729ad84eded87fef69004d89249170d44" },
            new TesseractLanguage { Filename = "hrv.traineddata.gz", Code = "hrv", LangName = "Croatian", Size = 3.33, Sha1 = "b05db705553607afe3d3f2385dc7f272f348a59c" },
            new TesseractLanguage { Filename = "hun.traineddata.gz", Code = "hun", LangName = "Hungarian", Size = 4.62, Sha1 = "250f8b5ad6464e3f0ad8694c0b54392cf6c9d73b" },
            new TesseractLanguage { Filename = "iku.traineddata.gz", Code = "iku", LangName = "Inuktitut", Size = 0.30, Sha1 = "119af8b174547aa9cb00f04512d4960d523863ad" },
            new TesseractLanguage { Filename = "ind.traineddata.gz", Code = "ind", LangName = "Indonesian", Size = 2.51, Sha1 = "f46f56473ba850408499678c349bdb6dc544dc67" },
            new TesseractLanguage { Filename = "isl.traineddata.gz", Code = "isl", LangName = "Icelandic", Size = 2.28, Sha1 = "54004c851361c36ddf48b4443caf79188fa757b6" },
            new TesseractLanguage { Filename = "ita.traineddata.gz", Code = "ita", LangName = "Italian", Size = 5.40, Sha1 = "1730f0e32cad3bd76a4f58de67d7c8e2cde17b51" },
            new TesseractLanguage { Filename = "ita_old.traineddata.gz", Code = "ita_old", LangName = "Italian (Old)", Size = 5.35, Sha1 = "b7a4293b464cbcce08fd5dc15a9831cff888cdf0" },
            new TesseractLanguage { Filename = "jav.traineddata.gz", Code = "jav", LangName = "Javanese", Size = 1.60, Sha1 = "3caa600f063705a2649be289038f381ecdaa8989" },
            new TesseractLanguage { Filename = "jpn.traineddata.gz", Code = "jpn", LangName = "Japanese", Size = 13.65, Sha1 = "7545927e6c60888a61556af4247e81c7a08cc17d" },
            new TesseractLanguage { Filename = "kan.traineddata.gz", Code = "kan", LangName = "Kannada", Size = 15.12, Sha1 = "53d26da4fde19b5663f4e7748809ba4baf12fe96" },
            new TesseractLanguage { Filename = "kat.traineddata.gz", Code = "kat", LangName = "Georgian", Size = 2.23, Sha1 = "8c48267883781ad2278f052259fe4094c64ef9bb" },
            new TesseractLanguage { Filename = "kat_old.traineddata.gz", Code = "kat_old", LangName = "Georgian (Old)", Size = 0.19, Sha1 = "88e8312c3fc30ba03811d5d571e44158bc0ab5bf" },
            new TesseractLanguage { Filename = "kaz.traineddata.gz", Code = "kaz", LangName = "Kazakh", Size = 1.65, Sha1 = "45c6603afcfe4d81990439df3bed13dd1b4c654b" },
            new TesseractLanguage { Filename = "khm.traineddata.gz", Code = "khm", LangName = "Central Khmer", Size = 20.96, Sha1 = "d5a542959114b154db4db61419cd57aba1e3cf5a" },
            new TesseractLanguage { Filename = "kir.traineddata.gz", Code = "kir", LangName = "Kirghiz", Size = 2.02, Sha1 = "ee9ba20cde7597688140fc43b14e49417d1052b7" },
            new TesseractLanguage { Filename = "kor.traineddata.gz", Code = "kor", LangName = "Korean", Size = 5.11, Sha1 = "39b452ede31b196c66442ea580b5664377eabdab" },
            new TesseractLanguage { Filename = "kur.traineddata.gz", Code = "kur", LangName = "Kurdish", Size = 0.73, Sha1 = "a36683c3f62415e1d12529b7642b9463c880db0c", RTL = true },
            new TesseractLanguage { Filename = "lao.traineddata.gz", Code = "lao", LangName = "Lao", Size = 8.70, Sha1 = "95dbad397571d2d2c13ed63ddc16a51fca343cfb" },
            new TesseractLanguage { Filename = "lat.traineddata.gz", Code = "lat", LangName = "Latin", Size = 2.04, Sha1 = "43dc27088ecce88915f6de15c7f6ec9037eebfee" },
            new TesseractLanguage { Filename = "lav.traineddata.gz", Code = "lav", LangName = "Latvian", Size = 2.91, Sha1 = "db4e13d875a4c88bd6d8873a7db95fcbd7f9114b" },
            new TesseractLanguage { Filename = "lit.traineddata.gz", Code = "lit", LangName = "Lithuanian", Size = 3.28, Sha1 = "fae20b8933a2c49fb9d98539299c7452d530514a" },
            new TesseractLanguage { Filename = "mal.traineddata.gz", Code = "mal", LangName = "Malayalam", Size = 3.49, Sha1 = "77a6553e0a37ddf5935a4e81b918850b8babb379" },
            new TesseractLanguage { Filename = "mar.traineddata.gz", Code = "mar", LangName = "Marathi", Size = 5.85, Sha1 = "36297ba7adad4e476815a1ab962b556994e85196" },
            new TesseractLanguage { Filename = "mkd.traineddata.gz", Code = "mkd", LangName = "Macedonian", Size = 1.36, Sha1 = "63a9ce25d9e2ce9e169ac17e422564809be21fb2" },
            new TesseractLanguage { Filename = "mlt.traineddata.gz", Code = "mlt", LangName = "Maltese", Size = 1.96, Sha1 = "18cb93ee612c4c7989c005cdf3a228c4e524db67" },
            new TesseractLanguage { Filename = "msa.traineddata.gz", Code = "msa", LangName = "Malay", Size = 2.47, Sha1 = "a40a2af1a06db7cbf4ecef903bff645d7ee3cfc3" },
            new TesseractLanguage { Filename = "mya.traineddata.gz", Code = "mya", LangName = "Burmese", Size = 29.36, Sha1 = "f5875d22dc164da4176856ced8521790dfa986a8" },
            new TesseractLanguage { Filename = "nep.traineddata.gz", Code = "nep", LangName = "Nepali", Size = 6.53, Sha1 = "55940992c6269123a49c0f0f616d766f9cb3aa4c" },
            new TesseractLanguage { Filename = "nld.traineddata.gz", Code = "nld", LangName = "Dutch", Size = 6.83, Sha1 = "7a19402e128c97ffb5044780c055344e4b92cceb" },
            new TesseractLanguage { Filename = "nor.traineddata.gz", Code = "nor", LangName = "Norwegian", Size = 3.14, Sha1 = "33fd288a93a5260954b0fca37894ce50d8872971" },
            new TesseractLanguage { Filename = "ori.traineddata.gz", Code = "ori", LangName = "Oriya", Size = 3.06, Sha1 = "cc4951bf162f3e06f83a7f63868dc0ba2a86c83c" },
//            new TesseractLanguage { Filename = "osd.traineddata.gz", Code = "osd", LangName = "", Size = 4.08, Sha1 = "d8c10c1fca9b954ca2500e6abeee94b50329f486" },
            new TesseractLanguage { Filename = "pan.traineddata.gz", Code = "pan", LangName = "Panjabi", Size = 4.06, Sha1 = "ec846c1a93576f85878de4b06fa82241782cf2a4" },
            new TesseractLanguage { Filename = "pol.traineddata.gz", Code = "pol", LangName = "Polish", Size = 5.41, Sha1 = "55a31b8724722219ce80f0a75685f267ae221d3d" },
            new TesseractLanguage { Filename = "por.traineddata.gz", Code = "por", LangName = "Portuguese", Size = 5.06, Sha1 = "c486d3ba8ad2d7555f894352313f4c5cfb287dca" },
            new TesseractLanguage { Filename = "pus.traineddata.gz", Code = "pus", LangName = "Pushto", Size = 0.88, Sha1 = "c45f471412ae0a7b4ed92141c828963911fa5f15" },
            new TesseractLanguage { Filename = "ron.traineddata.gz", Code = "ron", LangName = "Romanian", Size = 2.99, Sha1 = "e21ef667ff7bb90904cf0d731ebe184854cde616" },
            new TesseractLanguage { Filename = "rus.traineddata.gz", Code = "rus", LangName = "Russian", Size = 6.05, Sha1 = "96d7897ddecc7f944b5c1751e9ff44416cc3ee21" },
            new TesseractLanguage { Filename = "san.traineddata.gz", Code = "san", LangName = "Sanskrit", Size = 9.52, Sha1 = "c324b96fc4f1dcd2295329081f18be98e1c71053" },
            new TesseractLanguage { Filename = "sin.traineddata.gz", Code = "sin", LangName = "Sinhala", Size = 2.60, Sha1 = "145f8b7da56fe12340d4a0ce3f0c1385e437398c" },
            new TesseractLanguage { Filename = "slk.traineddata.gz", Code = "slk", LangName = "Slovakian", Size = 3.45, Sha1 = "abe9737fb49c9284a10cbb87b9efa773234af5c3" },
            new TesseractLanguage { Filename = "slk_frak.traineddata.gz", Code = "slk_frak", LangName = "Slovakian (Fraktur)", Size = 0.28, Sha1 = "e12b4fd2b4d2739656ed28142ba5db081d49fce2" },
            new TesseractLanguage { Filename = "slv.traineddata.gz", Code = "slv", LangName = "Slovenian", Size = 2.47, Sha1 = "d94468d01fec2bbcb8be23e97ec5329ef58c541f" },
            new TesseractLanguage { Filename = "spa.traineddata.gz", Code = "spa", LangName = "Spanish", Size = 6.31, Sha1 = "89160dbb92dbb5bcd6c48237315f6aa892450ef1" },
            new TesseractLanguage { Filename = "spa_old.traineddata.gz", Code = "spa_old", LangName = "Spanish (Old)", Size = 6.57, Sha1 = "9d13656da6a91ca4717f9235340f0304c7f77110" },
            new TesseractLanguage { Filename = "sqi.traineddata.gz", Code = "sqi", LangName = "Albanian", Size = 2.40, Sha1 = "30957e11c55610634dfdd2704ff0d6036c2e4ca5" },
            new TesseractLanguage { Filename = "srp.traineddata.gz", Code = "srp", LangName = "Serbian", Size = 1.56, Sha1 = "5a7ef0c3c37d7f1891bde5a96b92b2fd3e48783a" },
            new TesseractLanguage { Filename = "srp_latn.traineddata.gz", Code = "srp_latn", LangName = "Serbian (Latin)", Size = 2.27, Sha1 = "2aa8ff0e22440d3aab1a59e47b416bcd7ab2e7ae" },
            new TesseractLanguage { Filename = "swa.traineddata.gz", Code = "swa", LangName = "Swahili", Size = 1.43, Sha1 = "6010b9255c1cd98c8bda39cd18904bf7782942e1" },
            new TesseractLanguage { Filename = "swe.traineddata.gz", Code = "swe", LangName = "Swedish", Size = 3.64, Sha1 = "1bd6fd11f36b3ca04342a521773179269c5410e3" },
            new TesseractLanguage { Filename = "syr.traineddata.gz", Code = "syr", LangName = "Syriac", Size = 1.06, Sha1 = "01aa53fd62897bcbfc053401405485d6f6aa9df9" },
            new TesseractLanguage { Filename = "tam.traineddata.gz", Code = "tam", LangName = "Tamil", Size = 1.99, Sha1 = "eaca5e8c91d7995894ff2dafc4b824f305d6fff0" },
            new TesseractLanguage { Filename = "tel.traineddata.gz", Code = "tel", LangName = "Telugu", Size = 16.81, Sha1 = "1f5b1e2f3d8a772b406e4a2b9d8ec38f1eec4cc6" },
            new TesseractLanguage { Filename = "tgk.traineddata.gz", Code = "tgk", LangName = "Tajik", Size = 0.40, Sha1 = "b839d70a88e1dc2a019d1b7e76b83e5dcb0df440" },
            new TesseractLanguage { Filename = "tgl.traineddata.gz", Code = "tgl", LangName = "Tagalog", Size = 1.56, Sha1 = "0bdbb9e5f763ebfeef8fc9cd0ba1913bd7309755" },
            new TesseractLanguage { Filename = "tha.traineddata.gz", Code = "tha", LangName = "Thai", Size = 5.61, Sha1 = "7a171182716c99c19c1cc9b934a70ef5bee7893a" },
            new TesseractLanguage { Filename = "tir.traineddata.gz", Code = "tir", LangName = "Tigrinya", Size = 0.60, Sha1 = "4292700b180a505c4a45666a13eac6e144b48615" },
            new TesseractLanguage { Filename = "tur.traineddata.gz", Code = "tur", LangName = "Turkish", Size = 5.61, Sha1 = "8d72dc5ec5f22073f6b3ae2f79534e36aa8f63e8" },
            new TesseractLanguage { Filename = "uig.traineddata.gz", Code = "uig", LangName = "Uighur", Size = 0.72, Sha1 = "d20262f24476229539b4b87efa9327428052b241" },
            new TesseractLanguage { Filename = "ukr.traineddata.gz", Code = "ukr", LangName = "Ukrainian", Size = 2.92, Sha1 = "0871744dfacfa446e212e5c7e671c790b5fdd2f0" },
            new TesseractLanguage { Filename = "urd.traineddata.gz", Code = "urd", LangName = "Urdu", Size = 1.83, Sha1 = "be2964ca83114ee04b3a258e71525b8a1a670c97", RTL = true },
            new TesseractLanguage { Filename = "uzb.traineddata.gz", Code = "uzb", LangName = "Uzbek", Size = 1.55, Sha1 = "8de3127c90628514d61c0ded9510d4b2728f4b69" },
            new TesseractLanguage { Filename = "uzb_cyrl.traineddata.gz", Code = "uzb_cyrl", LangName = "Uzbek (Cyrillic)", Size = 1.19, Sha1 = "e1190d147d6ce3770d768724c82e103b06c93061" },
            new TesseractLanguage { Filename = "vie.traineddata.gz", Code = "vie", LangName = "Vietnamese", Size = 2.27, Sha1 = "571e132cd3ed26f5c33943efe7aa17835d277a15" },
            new TesseractLanguage { Filename = "yid.traineddata.gz", Code = "yid", LangName = "Yiddish", Size = 1.60, Sha1 = "0dbb6e19b660b57283f954eb5183cc2f3677fdda" },
        };

        #endregion
    }
}