namespace W_TB_MS.Models
{
    internal sealed record CurveArchivePair(string LogPath, string ExcelPath);

    internal static class CurveArchiveWriter
    {
        internal static CurveArchivePair SavePair(string archiveFolder, CurveHistoryData snapshot)
        {
            return SavePairCore(
                archiveFolder,
                snapshot.Times,
                logPath => CurveHistoryFile.SaveLog(logPath, snapshot),
                excelPath => CurveHistoryFile.SaveExcel(excelPath, snapshot));
        }

        internal static CurveArchivePair SavePair(string archiveFolder, CompactCurveHistoryData snapshot)
        {
            return SavePairCore(
                archiveFolder,
                snapshot.Times,
                logPath => CurveHistoryFile.SaveLog(logPath, snapshot),
                excelPath => CurveHistoryFile.SaveExcel(excelPath, snapshot));
        }

        private static CurveArchivePair SavePairCore(
            string archiveFolder,
            IReadOnlyList<DateTime> times,
            Action<string> saveLog,
            Action<string> saveExcel)
        {
            Directory.CreateDirectory(archiveFolder);
            DateTime firstSample = times[0];
            DateTime lastSample = times[^1];
            string fileName = $"热泵曲线_{firstSample:yyyyMMdd_HHmmss}-{lastSample:yyyyMMdd_HHmmss}";
            string basePath = Path.Combine(archiveFolder, fileName);
            int duplicate = 2;
            while (File.Exists(basePath + ".log") || File.Exists(basePath + ".xlsx"))
                basePath = Path.Combine(archiveFolder, $"{fileName}_{duplicate++}");

            string logPath = basePath + ".log";
            string excelPath = basePath + ".xlsx";
            string temporaryBasePath = basePath + $".tmp-{Guid.NewGuid():N}";
            string temporaryLogPath = temporaryBasePath + ".log";
            string temporaryExcelPath = temporaryBasePath + ".xlsx";
            bool logCommitted = false;

            try
            {
                saveLog(temporaryLogPath);
                saveExcel(temporaryExcelPath);
                File.Move(temporaryLogPath, logPath);
                logCommitted = true;
                File.Move(temporaryExcelPath, excelPath);
                return new CurveArchivePair(logPath, excelPath);
            }
            catch
            {
                if (logCommitted)
                    File.Delete(logPath);
                throw;
            }
            finally
            {
                File.Delete(temporaryLogPath);
                File.Delete(temporaryExcelPath);
            }
        }
    }
}
