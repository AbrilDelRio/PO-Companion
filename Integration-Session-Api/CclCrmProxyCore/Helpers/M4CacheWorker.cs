using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Web;

namespace CclCrmProxyCore.Helpers
{
    public static class M4CacheWorker
    {
        static readonly ObjectCache Cache = MemoryCache.Default;
        private static int DaysToCache = 1;

        public static void AddObjectToCache(object obj, string key)
        {
            Cache.Add(key, obj, DateTime.Now.AddDays(DaysToCache));
        }

        public static T GetObjectFromCache<T>(string key) where T : class
        {
            try
            {
                return (T)Cache[key];
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Remove item from cache
        /// </summary>
        public static void Clear(string key)
        {
            Cache.Remove(key);
        }

        /// <summary>
        /// Check for item in cache
        /// </summary>
        public static bool Exists(string key)
        {
            return Cache.Get(key) != null;
        }

        /// <summary>
        /// Gets all cached items as a list by their key.
        /// </summary>
        /// <returns></returns>
        public static List<string> GetAllKeys()
        {
            return Cache.Select(keyValuePair => keyValuePair.Key).ToList();
        }
    }
}