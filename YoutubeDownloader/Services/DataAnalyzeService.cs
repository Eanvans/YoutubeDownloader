using System;
using System.Collections.Generic;
using System.Linq;
using static YoutubeDownloader.ViewModels.Components.DashboardViewModel;

namespace YoutubeDownloader.Services
{
    public static class DataAnalyzeService
    {
        private static TimeSpan DEFAULT_TIME_INTERVAL = TimeSpan.FromMinutes(5);
        public static double THRES_COFF = 1.1;

        /// <summary>
        /// 使用滑动滤波的方式过滤峰值
        /// </summary>
        /// <param name="root"></param>
        /// <param name="interval"></param>
        /// <returns></returns>
        public static List<VodCommentData> FindHotCommentsIntervalSlidingFilter(
            List<ChatLog> rootRaw,
            TimeSpan? interval = null
        )
        {
            if (interval == null)
                interval = DEFAULT_TIME_INTERVAL;

            int commentsCount = rootRaw.Count;

            // 处理数据里面小于0的部分
            List<ChatLog> root = new();
            foreach (ChatLog log in rootRaw)
            {
                string ts = log.Timestamp ?? "0";
                if (!ts.Contains("-"))
                {
                    root.Add(log);
                }
            }

            var commentsSecList = root.Select(s => ConvertToTimestampExtended(s.Timestamp ?? "0"))
                .ToList();
            int secondsDt = 5; // 5s

            // 计算 T 的最大值
            double startSecond = commentsSecList.FirstOrDefault();
            double endSecond = commentsSecList.LastOrDefault();
            double maxTime = endSecond - startSecond + secondsDt;

            // 构建 T 并初始化 区间大小
            int intervalLen = (int)(maxTime / secondsDt) + 1;
            double[] T = new double[intervalLen];
            double[] commentCountByDt = new double[intervalLen];
            for (int i = 0; i < intervalLen; i++)
            {
                T[i] = i * secondsDt;
            }

            // 分配数据到区间内
            for (int i = 0; i < commentsSecList.Count; i++)
            {
                double timeOffset = commentsSecList[i] - startSecond;
                int k = (int)Math.Floor(timeOffset / secondsDt);
                if (k >= 0 && k < intervalLen)
                {
                    commentCountByDt[k]++;
                }
                else
                {
                    Console.WriteLine(
                        $"Warning: time value {commentsSecList[i]} is out of T range."
                    );
                }
            }

            // 第一步：计算窗口长度
            // default time is 3min
            var tWindowLength = (int)(3 * 60) / secondsDt;
            // 第二步：调用 MeanFilter（需要前面定义的函数）平滑窗口
            double[]? filteredCount = null;
            try
            {
                filteredCount = AlgoService.MeanFilter(commentCountByDt, tWindowLength + 1);
            }
            catch (Exception)
            {
                return new();
            }
            // 第三步：对结果进行缩放
            double scale = tWindowLength + 1;
            double[] scaledFilteredCount = new double[filteredCount.Length];
            for (int i = 0; i < filteredCount.Length; i++)
            {
                scaledFilteredCount[i] = filteredCount[i] * scale;
            }

            // 第四步：截取 T 中间部分：T1 = T(1 + WL/2 : end - WL/2)
            int startIdx = (int)(tWindowLength / 2.0); // MATLAB 是从 1 开始索引，所以这里是 1 + wl/2 - 1
            int endIdx = T.Length - (int)(tWindowLength / 2.0) - 1;

            int resultLength = endIdx - startIdx + 1;
            double[] T1 = new double[resultLength];
            Array.Copy(T, startIdx, T1, 0, resultLength);

            DetectPeaks(
                scaledFilteredCount,
                tWindowLength,
                out List<int> peakIndex,
                out List<double> peak,
                out double meanVal
            );

            // peakTrue 是峰值
            FilterTruePeaks(
                peakIndex,
                peak,
                tWindowLength,
                out List<int> peakIndexTrue,
                out List<double> peakTrue
            );

            // 提取 peakT：从 T1 中取出对应索引的时间值 加上第一个值的second offset 才是准确的时间值
            List<VodCommentData> rst = new();
            var offsetSec = commentsSecList.FirstOrDefault();
            int peakI = 0;
            foreach (int index in peakIndexTrue)
            {
                rst.Add(
                    new VodCommentData()
                    {
                        TimeInterval = "7min",
                        CommentsScore = peakTrue[peakI++],
                        OffsetSeconds = T1[index] + offsetSec,
                    }
                );
            }

            return rst;
        }

        public static void DetectPeaks(
            double[] count1,
            int tWindowLength,
            out List<int> peakIndex,
            out List<double> peak,
            out double meanVal
        )
        {
            // 计算阈值：1.3 * mean(count1)
            double sum = 0;
            foreach (var val in count1)
            {
                sum += val;
            }
            meanVal = sum / count1.Length;
            double thr = THRES_COFF * meanVal; //调整这个值来探测更多的峰值

            peakIndex = new List<int>();
            peak = new List<double>();

            for (int i = 0; i <= count1.Length - tWindowLength - 1; i++)
            {
                // 提取窗口数据
                double[] tmpData = new double[tWindowLength + 1];
                Array.Copy(count1, i, tmpData, 0, tWindowLength + 1);

                // 找最大值
                double maxVal = double.MinValue;
                int maxIdxInWindow = -1;
                for (int j = 0; j < tmpData.Length; j++)
                {
                    if (tmpData[j] > maxVal)
                    {
                        maxVal = tmpData[j];
                        maxIdxInWindow = j;
                    }
                }

                // 跳过未超过阈值的情况
                if (maxVal < thr)
                    continue;

                // 排除窗口边缘的极值点
                if (maxIdxInWindow == 0 || maxIdxInWindow == tmpData.Length - 1)
                    continue;

                // 全局索引
                int globalIndex = i + maxIdxInWindow;

                // 判断是否已经记录了这个峰值
                if (peakIndex.Count == 0)
                {
                    peakIndex.Add(globalIndex);
                    peak.Add(maxVal);
                }
                else if (globalIndex == peakIndex[peakIndex.Count - 1])
                {
                    continue; // 避免重复添加
                }
                else
                {
                    peakIndex.Add(globalIndex);
                    peak.Add(maxVal);
                }
            }
        }

        public static void FilterTruePeaks(
            List<int> peakIndex,
            List<double> peak,
            int tWindowLength,
            out List<int> peakIndexTrue,
            out List<double> peakTrue
        )
        {
            peakIndexTrue = new List<int>();
            peakTrue = new List<double>();

            for (int i = 0; i < peak.Count; i++)
            {
                // 创建临时列表并删除第 i 个元素
                List<int> peakIndexTmp = new List<int>(peakIndex);
                List<double> peakTmp = new List<double>(peak);

                peakIndexTmp.RemoveAt(i);
                peakTmp.RemoveAt(i);

                int nowIndex = peakIndex[i];
                double nowPeak = peak[i];

                bool isTrue = true;

                for (int j = 0; j < peakTmp.Count; j++)
                {
                    int distance = Math.Abs(peakIndexTmp[j] - nowIndex);

                    if (distance > tWindowLength)
                        continue;

                    if (peakTmp[j] > nowPeak)
                    {
                        isTrue = false;
                        break;
                    }
                }

                if (isTrue)
                {
                    peakIndexTrue.Add(nowIndex);
                    peakTrue.Add(nowPeak);
                }
            }
        }

        public static double ConvertToTimestampExtended(string timeStr)
        {
            string[] parts = timeStr.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
                throw new ArgumentException("时间格式必须为 hh:mm:ss 或 mm:ss");

            int hours = 0;
            int minutes,
                seconds;

            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[0], out hours))
                    throw new ArgumentException("无效的小时部分");
                if (!int.TryParse(parts[1], out minutes))
                    throw new ArgumentException("无效的分钟部分");
                if (!int.TryParse(parts[2], out seconds))
                    throw new ArgumentException("无效的秒部分");
            }
            else
            {
                if (!int.TryParse(parts[0], out minutes))
                    throw new ArgumentException("无效的分钟部分");
                if (!int.TryParse(parts[1], out seconds))
                    throw new ArgumentException("无效的秒部分");
            }

            return hours * 3600.0 + minutes * 60.0 + seconds;
        }
    }
}
