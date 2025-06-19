using System;

namespace YoutubeDownloader.Services
{
    public static class AlgoService
    {
        public static double[] MeanFilter(double[] S, int n)
        {
            if (S == null || S.Length < n || n <= 0)
                throw new ArgumentException("Invalid input or window size.");

            int len = S.Length;
            int resultLength = len - n + 1;
            double[] smoothS = new double[resultLength];

            for (int i = 0; i < resultLength; i++)
            {
                double sum = 0.0;
                for (int j = 0; j < n; j++)
                {
                    sum += S[i + j];
                }
                smoothS[i] = sum / n;
            }

            return smoothS;
        }
    }
}
