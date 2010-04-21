﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Hammock.Authentication;
using Hammock.Caching;
using Hammock.Extensions;
using Hammock.Retries;
using Hammock.Web.Mocks;
using Hammock.Serialization;
using Hammock.Tasks;
using Hammock.Web;
using Hammock.Streaming;

#if SILVERLIGHT
using Hammock.Silverlight.Compat;
#endif

namespace Hammock
{
#if !Silverlight
    [Serializable]
#endif
    public class RestClient : RestBase, IRestClient
    {
        private const string MockContentType = "mockContentType";
        private const string MockScheme = "mockScheme";
        private const string MockProtocol = "mock";
        private const string MockStatusDescription = "mockStatusDescription";
        private const string MockContent = "mockContent";
        private const string MockHttpMethod = "mockHttpMethod";

        public virtual string Authority { get; set; }

#if SILVERLIGHT
        public virtual bool HasElevatedPermissions { get; set; }
#endif

#if !Silverlight
        private bool _firstTry = true;
#endif
        private int _remainingRetries;
        private readonly object _timedTasksLock = new object();
        private readonly Dictionary<RestRequest, TimedTask> _tasks = new Dictionary<RestRequest, TimedTask>();

        static RestClient()
        {
            WebRequest.RegisterPrefix(
                MockProtocol, new MockWebRequestFactory()
                );
        }

#if !Silverlight
        public virtual RestResponse Request(RestRequest request)
        {
            var query = RequestImpl(request);

            return BuildResponseFromResult(request, query);
        }

        public virtual RestResponse<T> Request<T>(RestRequest request)
        {
            var query = RequestImpl(request);

            return BuildResponseFromResult<T>(request, query);
        }

        public RestResponse Request()
        {
            var query = RequestImpl(null);

            return BuildResponseFromResult(null, query);
        }

        public RestResponse<T> Request<T>()
        {
            var query = RequestImpl(null);

            return BuildResponseFromResult<T>(null, query);
        }

        private WebQuery RequestImpl(RestRequest request)
        {
            request = request ?? new RestRequest();
            var uri = request.BuildEndpoint(this);
            var query = GetQueryFor(request, uri);
            SetQueryMeta(request, query);
            var retryPolicy = GetRetryPolicy(request);
            if (_firstTry)
            {
                _remainingRetries = (retryPolicy != null ? retryPolicy.RetryCount : 0) + 1;
                _firstTry = false;
            }

            while (_remainingRetries > 0)
            {
                var url = uri.ToString();
                if (RequestExpectsMock(request))
                {
                    url = BuildMockRequestUrl(request, query, url);
                }

                WebException exception;
                if (!RequestWithCache(request, query, url, out exception) &&
                    !RequestMultiPart(request, query, url, out exception))
                {
                    query.Request(url, out exception);
                }

                query.Result.Exception = exception;
                var current = query.Result;

                if (retryPolicy != null)
                {
                    var retry = ShouldRetry(retryPolicy, exception, current);

                    if (retry)
                    {
                        _remainingRetries--;
                        if (_remainingRetries > 0)
                        {
                            query.Result = new WebQueryResult { PreviousResult = current };
                        }
                    }
                    else
                    {
                        _remainingRetries = 0;
                    }
                }
                else
                {
                    _remainingRetries = 0;
                }


            }

            _firstTry = _remainingRetries == 0;
            return query;
        }

        

        private bool RequestMultiPart(RestBase request, WebQuery query, string url, out WebException exception)
        {
            var parameters = GetPostParameters(request);
            if (parameters == null || parameters.Count() == 0)
            {
                exception = null;
                return false;
            }

            // [DC]: Default to POST if no method provided
            query.Method = query.Method != WebMethod.Post && Method != WebMethod.Put ? WebMethod.Post : query.Method;
            query.Request(url, parameters, out exception);
            return true;
        }

        private bool RequestWithCache(RestBase request, WebQuery query, string url, out WebException exception)
        {
            var cache = GetCache(request);
            if (cache == null)
            {
                exception = null;
                return false;
            }

            var options = GetCacheOptions(request);
            if (options == null)
            {
                exception = null;
                return false;
            }

            // [DC]: This is currently prefixed to the full URL
            var function = GetCacheKeyFunction(request);
            var key = function != null ? function.Invoke() : "";

            switch (options.Mode)
            {
                case CacheMode.NoExpiration:
                    query.Request(url, key, cache, out exception);
                    break;
                case CacheMode.AbsoluteExpiration:
                    var expiry = options.Duration.FromNow();
                    query.Request(url, key, cache, expiry, out exception);
                    break;
                case CacheMode.SlidingExpiration:
                    query.Request(url, key, cache, options.Duration, out exception);
                    break;
                default:
                    throw new NotSupportedException("Unknown CacheMode");
            }

            return true;
        }
#endif
        private static bool ShouldRetry(RetryPolicy retryPolicy,
                                        WebException exception,
                                        WebQueryResult current)
        {
            var retry = false;
            foreach (var condition in retryPolicy.RetryConditions.OfType<RetryErrorCondition>())
            {
                if (exception == null)
                {
                    continue;
                }
                retry |= condition.RetryIf(exception);
            }

            foreach (var condition in retryPolicy.RetryConditions.OfType<RetryResultCondition>())
            {
                if (current == null)
                {
                    continue;
                }
                retry |= condition.RetryIf(current);
            }

            foreach (var condition in retryPolicy.RetryConditions.OfType<IRetryCustomCondition>())
            {
                var innerType = condition.GetDeclaredTypeForGeneric(typeof(IRetryCondition<>));
                if (innerType == null)
                {
                    continue;
                }

                var retryType = typeof(RetryCustomCondition<>).MakeGenericType(innerType);
                if (retryType == null)
                {
                    continue;
                }

                var func = condition.GetValue("ConditionFunction") as MulticastDelegate;
                if (func == null)
                {
                    continue;
                }

                // Call the function to find the retry evaluator
#if !Smartphone
                var t = func.DynamicInvoke(null);
#else
                var del = func.GetInvocationList().FirstOrDefault();
                var t = del.Method.Invoke(func, null);
#endif

                // Invoke the retry predicate and pass the evaluator
                var p = condition.GetValue("RetryIf");
                var r = p.GetType().InvokeMember("Invoke",
                    BindingFlags.Public | BindingFlags.InvokeMethod | BindingFlags.Instance,
                    null, p, new[] { t });

                retry |= (bool)r;
            }

            return retry;
        }

        private string BuildMockRequestUrl(RestRequest request,
                                           WebQuery query,
                                           string url)
        {
            if (url.Contains("https"))
            {
                url = url.Replace("https", MockProtocol);

                query.Parameters.Add(MockScheme, "https");
            }
            if (url.Contains("http"))
            {
                url = url.Replace("http", MockProtocol);
                query.Parameters.Add(MockScheme, "http");
            }

            if (request.ExpectStatusCode.HasValue)
            {
                query.Parameters.Add("mockStatusCode", ((int)request.ExpectStatusCode.Value).ToString());
                if (request.ExpectStatusDescription.IsNullOrBlank())
                {
                    query.Parameters.Add(MockStatusDescription, request.ExpectStatusCode.ToString());
                }
            }
            if (!request.ExpectStatusDescription.IsNullOrBlank())
            {
                query.Parameters.Add(MockStatusDescription, request.ExpectStatusDescription);
            }

            query.Parameters.Add(
                MockHttpMethod, request.Method.ToString().ToUpper()
                );

            var expectEntity = SerializeExpectEntity(request);
            if (expectEntity != null)
            {
                query.Parameters.Add(MockContent, expectEntity.Content);
                query.Parameters.Add(MockContentType, expectEntity.ContentType);
                query.HasEntity = true; // Used with POSTs
            }
            else
            {
                if (!request.ExpectContent.IsNullOrBlank())
                {
                    query.Parameters.Add(MockContent, request.ExpectContent);
                    query.Parameters.Add(MockContentType,
                                         !request.ExpectContentType.IsNullOrBlank()
                                             ? request.ExpectContentType
                                             : "text/html"
                        );
                }
                else
                {
                    if (!request.ExpectContentType.IsNullOrBlank())
                    {
                        query.Parameters.Add(
                            MockContentType, request.ExpectContentType
                            );
                    }
                }
            }

            if (request.ExpectHeaders.Count > 0)
            {
                var names = new StringBuilder();
                var values = new StringBuilder();
                var count = 0;
                foreach (var key in request.ExpectHeaders.AllKeys)
                {
                    names.Append(key);
                    values.Append(request.ExpectHeaders[key].Value);
                    count++;
                    if (count < request.ExpectHeaders.Count)
                    {
                        names.Append(",");
                        values.Append(",");
                    }
                }

                query.Parameters.Add("mockHeaderNames", names.ToString());
                query.Parameters.Add("mockHeaderValues", values.ToString());
            }

            return url;
        }

        private static bool RequestExpectsMock(RestRequest request)
        {
            return request.ExpectEntity != null ||
                   request.ExpectHeaders.Count > 0 ||
                   request.ExpectStatusCode.HasValue ||
                   !request.ExpectContent.IsNullOrBlank() ||
                   !request.ExpectContentType.IsNullOrBlank() ||
                   !request.ExpectStatusDescription.IsNullOrBlank();
        }

        private ICache GetCache(RestBase request)
        {
            return request.Cache ?? Cache;
        }

        private IEnumerable<HttpPostParameter> GetPostParameters(RestBase request)
        {
            if (request.PostParameters != null)
            {
                foreach (var parameter in request.PostParameters)
                {
                    yield return parameter;
                }
            }

            if (PostParameters == null)
            {
                yield break;
            }

            foreach (var parameter in PostParameters)
            {
                yield return parameter;
            }
        }

        private CacheOptions GetCacheOptions(RestBase request)
        {
            return request.CacheOptions ?? CacheOptions;
        }

        private Func<string> GetCacheKeyFunction(RestBase request)
        {
            return request.CacheKeyFunction ?? CacheKeyFunction;
        }

        private string GetProxy(RestBase request)
        {
            return request.Proxy ?? Proxy;
        }

        private string GetUserAgent(RestBase request)
        {
            var userAgent = request.UserAgent.IsNullOrBlank()
                                ? UserAgent
                                : request.UserAgent;
            return userAgent;
        }

        private ISerializer GetSerializer(RestBase request)
        {
            return request.Serializer ?? Serializer;
        }

        private IWebCredentials GetWebCredentials(RestBase request)
        {
            var credentials = request.Credentials ?? Credentials;
            return credentials;
        }

        private IWebQueryInfo GetInfo(RestBase request)
        {
            var info = request.Info ?? Info;
            return info;
        }

        private TimeSpan? GetTimeout(RestBase request)
        {
            return request.Timeout ?? Timeout;
        }

        private WebMethod GetWebMethod(RestBase request)
        {
            var method = !request.Method.HasValue
                             ? !Method.HasValue
                                   ? WebMethod.Get
                                   : Method.Value
                             : request.Method.Value;

            return method;
        }

        private byte[] GetPostContent(RestBase request)
        {
            var content = request.PostContent ?? PostContent;
            return content;
        }

        private RetryPolicy GetRetryPolicy(RestBase request)
        {
            var policy = request.RetryPolicy ?? RetryPolicy;
            return policy;
        }

        private TaskOptions GetTaskOptions(RestBase request)
        {
            var options = request.TaskOptions ?? TaskOptions;
            return options;
        }

        private StreamOptions GetStreamOptions(RestBase request)
        {
            var options = request.StreamOptions ?? StreamOptions;
            return options;
        }

        private object GetTag(RestBase request)
        {
            var tag = request.Tag ?? Tag;
            return tag;
        }

        public virtual IAsyncResult BeginRequest(RestRequest request, RestCallback callback, object userState)
        {
            return BeginRequest(request, callback, null, null, false /* isInternal */, userState);
        }

        public virtual IAsyncResult BeginRequest<T>(RestRequest request, RestCallback<T> callback, object userState)
        {
            return BeginRequest(request, callback, null, null, false /* isInternal */, null);
        }

        public IAsyncResult BeginRequest()
        {
            return BeginRequest(null /* request */, null /* callback */);
        }

        public IAsyncResult BeginRequest<T>()
        {
            return BeginRequest(null /* request */, null /* callback */);
        }

        public virtual IAsyncResult BeginRequest(RestRequest request, RestCallback callback)
        {
            return BeginRequest(request, callback, null, null, false /* isInternal */, null);
        }

        public virtual IAsyncResult BeginRequest<T>(RestRequest request, RestCallback<T> callback)
        {
            return BeginRequest(request, callback, null, null, false /* isInternal */, null);
        }

        public virtual IAsyncResult BeginRequest(RestCallback callback)
        {
            return BeginRequest(null, callback, null, null, false /* isInternal */, null);
        }

        public virtual IAsyncResult BeginRequest(RestRequest request)
        {
            return BeginRequest(request, null, null);
        }

        public IAsyncResult BeginRequest(RestRequest request, object userState)
        {
            return BeginRequest(request, null, userState);
        }

        public virtual IAsyncResult BeginRequest<T>(RestRequest request)
        {
            return BeginRequest<T>(request, null, null);
        }

        public IAsyncResult BeginRequest<T>(RestRequest request, object userState)
        {
            return BeginRequest<T>(request, null, userState);
        }

        public virtual IAsyncResult BeginRequest<T>(RestCallback<T> callback)
        {
            return BeginRequest(null, callback, null);
        }

        // Pattern: http://msdn.microsoft.com/en-us/library/ms228963.aspx
        public virtual RestResponse EndRequest(IAsyncResult result)
        {
            var webResult = EndRequestImpl(result);
            return webResult.AsyncState as RestResponse;
        }

        public virtual RestResponse<T> EndRequest<T>(IAsyncResult result)
        {
            var webResult = EndRequestImpl<T>(result);
            return webResult.AsyncState as RestResponse<T>;
        }

        private WebQueryAsyncResult EndRequestImpl(IAsyncResult result)
        {
            var webResult = result as WebQueryAsyncResult;
            if (webResult == null)
            {
                throw new InvalidOperationException("The IAsyncResult provided was not for this operation.");
            }

            var tag = (Triplet<RestRequest, RestCallback, object>)webResult.Tag;

            if (RequestExpectsMock(tag.First))
            {
                // [DC]: Mock results come via InnerResult
                webResult = (WebQueryAsyncResult)webResult.InnerResult;
            }

            if (webResult.CompletedSynchronously)
            {
                var query = webResult.AsyncState as WebQuery;
                if (query != null)
                {
                    // [DC]: From cache
                    CompleteWithQuery(query, tag.First, tag.Second, webResult);
                }
                else
                {
                    // [DC]: From mocks
                    webResult = CompleteWithMockWebResponse(result, webResult, tag);
                }
            }

            if (!webResult.IsCompleted)
            {
                webResult.AsyncWaitHandle.WaitOne();
            }
            return webResult;
        }

        private WebQueryAsyncResult EndRequestImpl<T>(IAsyncResult result)
        {
            var webResult = result as WebQueryAsyncResult;
            if (webResult == null)
            {
                throw new InvalidOperationException("The IAsyncResult provided was not for this operation.");
            }

            var tag = (Triplet<RestRequest, RestCallback<T>, object>)webResult.Tag;

            if (RequestExpectsMock(tag.First))
            {
                // [DC]: Mock results come via InnerResult
                webResult = (WebQueryAsyncResult)webResult.InnerResult;
            }

            if (webResult.CompletedSynchronously)
            {
                var query = webResult.AsyncState as WebQuery;
                if (query != null)
                {
                    // [DC]: From cache
                    CompleteWithQuery(query, tag.First, tag.Second, webResult);
                }
                else
                {
                    // [DC]: From mocks
                    webResult = CompleteWithMockWebResponse(result, webResult, tag);
                }
            }

            if (!webResult.IsCompleted)
            {
                webResult.AsyncWaitHandle.WaitOne();
            }
            return webResult;
        }

        private WebQueryAsyncResult CompleteWithMockWebResponse<T>(
            IAsyncResult result,
            IAsyncResult webResult,
            Triplet<RestRequest, RestCallback<T>, object> tag)
        {
            var webResponse = (WebResponse)webResult.AsyncState;
            var restRequest = tag.First;
            var userState = tag.Third;

            string content;
            using (var stream = webResponse.GetResponseStream())
            {
                using (var reader = new StreamReader(stream))
                {
                    content = reader.ReadToEnd();
                }
            }

            var restResponse = new RestResponse<T>
                                   {
                                       Content = content,
                                       ContentType = webResponse.ContentType,
                                       ContentLength = webResponse.ContentLength,
                                       StatusCode = restRequest.ExpectStatusCode.HasValue
                                                        ? restRequest.ExpectStatusCode.Value
                                                        : 0,
                                       StatusDescription = restRequest.ExpectStatusDescription,
                                       ResponseUri = webResponse.ResponseUri,
                                       IsMock = true
                                   };

            foreach (var key in webResponse.Headers.AllKeys)
            {
                restResponse.Headers.Add(key, webResponse.Headers[key]);
            }

            var deserializer = restRequest.Deserializer ?? Deserializer;
            if (deserializer != null && !restResponse.Content.IsNullOrBlank())
            {
                restResponse.ContentEntity = deserializer.Deserialize<T>(restResponse.Content);
            }

            TraceResponseWithMock(restResponse);

            var parentResult = (WebQueryAsyncResult)result;
            parentResult.AsyncState = restResponse;
            parentResult.IsCompleted = true;

            var callback = tag.Second;
            if (callback != null)
            {
                callback.Invoke(restRequest, restResponse, userState);
            }
            parentResult.Signal();
            return parentResult;
        }

        private WebQueryAsyncResult CompleteWithMockWebResponse(
            IAsyncResult result,
            IAsyncResult webResult,
            Triplet<RestRequest, RestCallback, object> tag)
        {
            var webResponse = (WebResponse)webResult.AsyncState;
            var restRequest = tag.First;
            var userState = tag.Third;

            string content;
            using (var stream = webResponse.GetResponseStream())
            {
                using (var reader = new StreamReader(stream))
                {
                    content = reader.ReadToEnd();
                }
            }

            var restResponse = new RestResponse
                                   {
                                       Content = content,
                                       ContentType = webResponse.ContentType,
                                       ContentLength = webResponse.ContentLength,
                                       StatusCode = restRequest.ExpectStatusCode.HasValue
                                                        ? restRequest.ExpectStatusCode.Value
                                                        : 0,
                                       StatusDescription = restRequest.ExpectStatusDescription,
                                       ResponseUri = webResponse.ResponseUri,
                                       IsMock = true
                                   };

            foreach (var key in webResponse.Headers.AllKeys)
            {
                restResponse.Headers.Add(key, webResponse.Headers[key]);
            }

            var deserializer = restRequest.Deserializer ?? Deserializer;
            if (deserializer != null && !restResponse.Content.IsNullOrBlank() && restRequest.ResponseEntityType != null)
            {
                restResponse.ContentEntity = deserializer.Deserialize(restResponse.Content, restRequest.ResponseEntityType);
            }

            TraceResponseWithMock(restResponse);

            var parentResult = (WebQueryAsyncResult)result;
            parentResult.AsyncState = restResponse;
            parentResult.IsCompleted = true;

            var callback = tag.Second;
            if (callback != null)
            {
                callback.Invoke(restRequest, restResponse, userState);
            }
            parentResult.Signal();
            return parentResult;
        }

        private static void TraceResponseWithMock(RestResponseBase restResponse)
        {
#if TRACE
            Trace.WriteLine(String.Concat(
                "RESPONSE: ", restResponse.StatusCode, " ", restResponse.StatusDescription)
                );
            Trace.WriteLineIf(restResponse.Headers.AllKeys.Count() > 0, "HEADERS:");
            foreach (var trace in restResponse.Headers.AllKeys.Select(
                key => String.Concat("\t", key, ": ", restResponse.Headers[key])))
            {
                Trace.WriteLine(trace);
            }
            Trace.WriteLine(String.Concat(
                "BODY: ", restResponse.Content)
                );
#endif
        }

        // TODO BeginRequest and BeginRequest<T> have too much duplication
        private IAsyncResult BeginRequest(RestRequest request,
                                          RestCallback callback,
                                          WebQuery query,
                                          string url,
                                          bool isInternal,
                                          object userState)
        {
            request = request ?? new RestRequest();
            if (!isInternal)
            {
                // [DC]: Recursive call possible, only do this once
                var uri = request.BuildEndpoint(this);
                query = GetQueryFor(request, uri);
                SetQueryMeta(request, query);
                url = uri.ToString();
            }

            if (RequestExpectsMock(request))
            {
                url = BuildMockRequestUrl(request, query, url);
            }

            var retryPolicy = GetRetryPolicy(request);
            _remainingRetries = (retryPolicy != null
                                     ? retryPolicy.RetryCount
                                     : 0);

            Func<WebQueryAsyncResult> beginRequest;
            WebQueryAsyncResult asyncResult;

            var streamOptions = GetStreamOptions(request);
            if (streamOptions != null)
            {
#if !SILVERLIGHT
                query.KeepAlive = true;
#endif
                var duration = streamOptions.Duration.HasValue
                                   ? streamOptions.Duration.Value
                                   : TimeSpan.Zero;

                var resultCount = streamOptions.ResultsPerCallback.HasValue
                                      ? streamOptions.ResultsPerCallback.Value
                                      : 10;

                beginRequest = () => BeginRequestStreamFunction(
                   request, query, url, callback, duration, resultCount, userState
                   );

                asyncResult = beginRequest.Invoke();
            }
            else
            {
                beginRequest
               = () => BeginRequestFunction(isInternal,
                       request,
                       query,
                       url,
                       callback,
                       userState);

                asyncResult = beginRequest.Invoke();
            }

            WebQueryResult previous = null;
            if (isInternal || (request.TaskOptions == null || request.TaskOptions.RepeatInterval.TotalMilliseconds == 0))
            {
                query.QueryResponse += (sender, args) =>
                                           {
                                               query.Result.PreviousResult = previous;
                                               var current = query.Result;

                                               if (retryPolicy != null)
                                               {
                                                   // [DC]: Query should already have exception applied
                                                   var exception = query.Result.Exception;
                                                   var retry = ShouldRetry(retryPolicy, exception, current);

                                                   if (retry)
                                                   {
                                                       previous = current;
                                                       BeginRequest(request, callback, query, url, true /* isInternal */, userState);
                                                       Interlocked.Decrement(ref _remainingRetries);
                                                   }
                                                   else
                                                   {
                                                       _remainingRetries = 0;
                                                   }
                                               }
                                               else
                                               {
                                                   _remainingRetries = 0;
                                               }

                                               query.Result = current;


                                               if (_remainingRetries == 0)
                                               {
                                                   CompleteWithQuery(query, request, callback, asyncResult);
                                               }
                                           };
            }
            return asyncResult;
        }
        private IAsyncResult BeginRequest<T>(RestRequest request,
                                             RestCallback<T> callback,
                                             WebQuery query,
                                             string url,
                                             bool isInternal,
                                             object userState)
        {
            request = request ?? new RestRequest();
            if (!isInternal)
            {
                var uri = request.BuildEndpoint(this);
                query = GetQueryFor(request, uri);
                SetQueryMeta(request, query);
                url = uri.ToString();
            }

            if (RequestExpectsMock(request))
            {
                url = BuildMockRequestUrl(request, query, url);
            }

            var retryPolicy = GetRetryPolicy(request);
            _remainingRetries = (retryPolicy != null
                                     ? retryPolicy.RetryCount
                                     : 0);

            Func<WebQueryAsyncResult> beginRequest;
            WebQueryAsyncResult asyncResult;

            var streamOptions = GetStreamOptions(request);
            if (streamOptions != null)
            {
#if !SILVERLIGHT
                query.KeepAlive = true;
#endif

                var duration = streamOptions.Duration.HasValue
                                   ? streamOptions.Duration.Value
                                   : TimeSpan.Zero;

                var resultCount = streamOptions.ResultsPerCallback.HasValue
                                      ? streamOptions.ResultsPerCallback.Value
                                      : 10;

                beginRequest = () => BeginRequestStreamFunction(
                   request, query, url, callback, duration, resultCount, userState
                   );

                asyncResult = beginRequest.Invoke();
            }
            else
            {
                beginRequest = () => BeginRequestFunction(
                   isInternal, request, query, url, callback, userState
                   );

                asyncResult = beginRequest.Invoke();
            }

            WebQueryResult previous = null;
            query.QueryResponse += (sender, args) =>
            {
                query.Result.PreviousResult = previous;
                var current = query.Result;

                if (retryPolicy != null)
                {
                    // [DC]: Query should already have exception applied
                    var exception = query.Result.Exception;
                    var retry = ShouldRetry(retryPolicy, exception, current);

                    if (retry)
                    {
                        previous = current;
                        BeginRequest(request, callback, query, url, true /* isInternal */, userState);
                        Interlocked.Decrement(ref _remainingRetries);
                    }
                    else
                    {
                        _remainingRetries = 0;
                    }
                }
                else
                {
                    _remainingRetries = 0;
                }

                query.Result = current;

                // [DC]: Callback is for a final result, not a retry
                if (_remainingRetries == 0)
                {
                    CompleteWithQuery(query, request, callback, asyncResult);
                }
            };

            return asyncResult;
        }

        private WebQueryAsyncResult BeginRequestFunction(bool isInternal,
                                                         RestRequest request,
                                                         WebQuery query,
                                                         string url,
                                                         RestCallback callback,
                                                         object userState)
        {
            WebQueryAsyncResult result;
            if (!isInternal)
            {
                if (!BeginRequestWithTask(request, callback, query, url, out result, userState))
                {
                    if (!BeginRequestWithCache(request, query, url, out result, userState))
                    {
                        if (!BeginRequestMultiPart(request, query, url, out result, userState))
                        {
                            // Normal operation
                            result = query.RequestAsync(url, userState);
                        }
                    }
                }
            }
            else
            {
                // Normal operation
                result = query.RequestAsync(url, userState);
            }

            result.Tag = new Triplet<RestRequest, RestCallback, object>
            {
                First = request,
                Second = callback,
                Third = userState
            };

            return result;
        }

        private void CompleteWithQuery<T>(WebQuery query,
                                          RestRequest request,
                                          RestCallback<T> callback,
                                          WebQueryAsyncResult result)
        {
            var response = BuildResponseFromResult<T>(request, query);
            if (query.IsStreaming)
            {
                return;
            }

            var wasStreaming = response.Content.Equals("END STREAMING");

            result.AsyncState = response;
            result.IsCompleted = true;
            if (callback != null && !wasStreaming)
            {
                callback.Invoke(request, response, query.UserState);
            }
            // Recurring tasks are only signalled when cancelled 
            // or when they reach their iteration limit
            lock (_timedTasksLock)
            {
                if (!_tasks.ContainsKey(request))
                {
                    result.Signal();
                }
            }
        }
        private void CompleteWithQuery(WebQuery query,
                                       RestRequest request,
                                       RestCallback callback,
                                       WebQueryAsyncResult result)
        {
            var response = BuildResponseFromResult(request, query);
            if (query.IsStreaming)
            {
                return;
            }

            var wasStreaming = response.Content.Equals("END STREAMING");

            result.AsyncState = response;
            result.IsCompleted = true;
            if (callback != null && !wasStreaming)
            {
                callback.Invoke(request, response, query.UserState);
            }
            //recurring tasks are only signalled when cancelled 
            //or when they reach their iteration limit
            lock (_timedTasksLock)
            {
                if (!_tasks.ContainsKey(request))
                {
                    result.Signal();
                }
            }
        }

        private WebQueryAsyncResult BeginRequestFunction<T>(bool isInternal,
                                                            RestRequest request,
                                                            WebQuery query,
                                                            string url,
                                                            RestCallback<T> callback,
                                                            object userState)
        {
            WebQueryAsyncResult result;
            if (!isInternal)
            {
                if (!BeginRequestWithTask(request, callback, query, url, out result, userState))
                {
                    if (!BeginRequestWithCache(request, query, url, out result, userState))
                    {
                        if (!BeginRequestMultiPart(request, query, url, out result, userState))
                        {
                            // Normal operation
                            result = query.RequestAsync(url, userState);
                        }
                    }
                }
            }
            else
            {
                // Normal operation
                result = query.RequestAsync(url, userState);
            }

            result.Tag = new Triplet<RestRequest, RestCallback<T>, object>
            {
                First = request,
                Second = callback,
                Third = userState
            };
            return result;
        }

        private static WebQueryAsyncResult BeginRequestStreamFunction<T>(RestRequest request,
                                                                         WebQuery query,
                                                                         string url,
                                                                         RestCallback<T> callback,
                                                                         TimeSpan duration,
                                                                         int resultsPerCallback,
                                                                         object userState)
        {
            var result = query.ExecuteStreamGetAsync(url, duration, resultsPerCallback);
            result.Tag = new Triplet<RestRequest, RestCallback<T>, object>
            {
                First = request,
                Second = callback,
                Third = userState
            };
            return result;
        }

        private static WebQueryAsyncResult BeginRequestStreamFunction(RestRequest request,
                                                                      WebQuery query,
                                                                      string url,
                                                                      RestCallback callback,
                                                                      TimeSpan duration,
                                                                      int resultsPerCallback,
                                                                      object userState)
        {
            var result = query.ExecuteStreamGetAsync(url, duration, resultsPerCallback);
            result.Tag = new Triplet<RestRequest, RestCallback, object>
            {
                First = request,
                Second = callback,
                Third = userState
            };
            return result;
        }

        private bool BeginRequestWithTask(RestRequest request,
                                          RestCallback callback,
                                          WebQuery query,
                                          string url,
                                          out WebQueryAsyncResult asyncResult,
                                          object userState)
        {
            var taskOptions = GetTaskOptions(request);
            if (taskOptions == null)
            {
                asyncResult = null;
                return false;
            }

            if (taskOptions.RepeatInterval <= TimeSpan.Zero)
            {
                asyncResult = null;
                return false;
            }

            TimedTask task;
#if !NETCF
            if (!taskOptions.GetType().IsGenericType)
            {
#endif
                // Tasks without rate limiting
                task = new TimedTask(taskOptions.DueTime,
                                      taskOptions.RepeatInterval,
                                      taskOptions.RepeatTimes,
                                      taskOptions.ContinueOnError,
                                      skip => BeginRequest(request,
                                                           callback,
                                                           query,
                                                           url,
                                                           true /* isInternal */,
                                                           userState
                                                           ));

#if !NETCF
            }
            else
            {
                // Tasks with rate limiting
                task = (TimedTask)BuildRateLimitingTask(request,
                                                 taskOptions,
                                                 callback,
                                                 query,
                                                 url,
                                                 userState);
            }
#endif

            RegisterTimedTaskForRequest(request, task);

            Action action = task.Start;

            var inner = action.BeginInvoke(ar => {/* No callback */}, null);

            asyncResult = new WebQueryAsyncResult { InnerResult = inner };
            task.AsyncResult = asyncResult;
            return true;
        }

        private void RegisterTimedTaskForRequest(RestRequest request, TimedTask task)
        {
            lock (_timedTasksLock)
            {
                if (_tasks.ContainsKey(request))
                {
                    throw new InvalidOperationException("Task already has a registered timed task");
                }
                task.Stopped += (s, e) => UnregisterTimedTaskForRequest(request);
                _tasks.Add(request, task);
            }
        }

        private void UnregisterTimedTaskForRequest(RestRequest request)
        {
            lock (_timedTasksLock)
            {
                if (_tasks.ContainsKey(request))
                {
                    var task = _tasks[request];
                    _tasks.Remove(request);
                    task.Dispose();
                }
            }
        }

        private bool BeginRequestWithTask<T>(RestRequest request,
                                          RestCallback<T> callback,
                                          WebQuery query,
                                          string url,
                                          out WebQueryAsyncResult asyncResult,
                                          object userState)
        {
            var taskOptions = GetTaskOptions(request);
            if (taskOptions == null)
            {
                asyncResult = null;
                return false;
            }

            if (taskOptions.RepeatInterval <= TimeSpan.Zero)
            {
                asyncResult = null;
                return false;
            }

            TimedTask task;
#if !NETCF
            if (!taskOptions.GetType().IsGenericType)
            {
#endif
                // Tasks without rate limiting
                task = new TimedTask(taskOptions.DueTime,
                                      taskOptions.RepeatInterval,
                                      taskOptions.RepeatTimes,
                                      taskOptions.ContinueOnError,
                                      skip => BeginRequest(request,
                                                           callback,
                                                           query,
                                                           url,
                                                           true /* isInternal */,
                                                           userState));
#if !NETCF
            }
            else
            {
                // Tasks with rate limiting
                task = (TimedTask)BuildRateLimitingTask(request,
                                                 taskOptions,
                                                 callback,
                                                 query,
                                                 url,
                                                 userState);

            }
#endif
            lock (_timedTasksLock)
            {
                _tasks[request] = task;
            }
            var action = new Action(task.Start);

            var inner = action.BeginInvoke(ar => { /* No callback */ }, null);
            asyncResult = new WebQueryAsyncResult { InnerResult = inner };
            task.AsyncResult = asyncResult;
            return true;
        }

#if !NETCF
        private object BuildRateLimitingTask(RestRequest request, ITaskOptions taskOptions, RestCallback callback, WebQuery query, string url, object userState)
        {
            var taskAction = new Action<bool>(skip =>
                                                  {
                                                      if (!skip)
                                                      {
                                                          BeginRequest(request, callback, query, url, true /* isInternal */, userState);
                                                      }
                                                      else
                                                      {
                                                          callback(request, 
                                                                   new RestResponse { SkippedDueToRateLimitingRule = true }, 
                                                                   userState);
                                                      }
                                                  });

            return BuildRateLimitingTaskImpl(taskOptions, taskAction);
        }

        private object BuildRateLimitingTask<T>(RestRequest request,
                                            ITaskOptions taskOptions,
                                            RestCallback<T> callback,
                                            WebQuery query,
                                            string url,
                                            object userState)
        {
            var taskAction = new Action<bool>(skip => BeginRequest(request,
                                                                   callback,
                                                                   query,
                                                                   url,
                                                                   true /* isInternal */,
                                                                   userState
                                                                   ));

            return BuildRateLimitingTaskImpl(taskOptions, taskAction);
        }

        private static object BuildRateLimitingTaskImpl(ITaskOptions taskOptions,
                                                        Action<bool> taskAction)
        {
            var innerType = taskOptions.GetDeclaredTypeForGeneric(typeof(ITaskOptions<>));
            var rateType = typeof(RateLimitingRule<>).MakeGenericType(innerType);
            var taskType = typeof(TimedTask<>).MakeGenericType(innerType);
            var rateLimitingType = (RateLimitType)taskOptions.GetValue("RateLimitType");

            object taskRule;
            var getRateLimitStatus = taskOptions.GetValue("GetRateLimitStatus");
            switch (rateLimitingType)
            {
                case RateLimitType.ByPercent:
                    var rateLimitingPercent = taskOptions.GetValue("RateLimitPercent");
                    if (getRateLimitStatus != null)
                    {
                        taskRule = Activator.CreateInstance(rateType, getRateLimitStatus, rateLimitingPercent);
                    }
                    else
                    {
                        taskRule = Activator.CreateInstance(rateType, rateLimitingPercent);
                    }
                    break;
                case RateLimitType.ByPredicate:
                    var rateLimitingPredicate = taskOptions.GetValue("RateLimitingPredicate");
                    if (getRateLimitStatus != null)
                    {
                        taskRule = Activator.CreateInstance(rateType, getRateLimitStatus, rateLimitingPredicate);
                    }
                    else
                    {
                        taskRule = Activator.CreateInstance(rateType, rateLimitingPredicate);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return Activator.CreateInstance(taskType,
                                            taskOptions.DueTime,
                                            taskOptions.RepeatInterval,
                                            taskOptions.RepeatTimes,
                                            taskOptions.ContinueOnError,
                                            taskAction,
                                            taskRule);
        }
#endif

        private bool BeginRequestMultiPart(RestBase request, WebQuery query, string url, out WebQueryAsyncResult result, object userState)
        {
            var parameters = GetPostParameters(request);
            if (parameters == null || parameters.Count() == 0)
            {
                result = null;
                return false;
            }

            // [DC]: Default to POST if no method provided
            query.Method = query.Method != WebMethod.Post && Method != WebMethod.Put ? WebMethod.Post : query.Method;
            result = query.RequestAsync(url, parameters);
            return true;
        }

        private bool BeginRequestWithCache(RestBase request, 
                                           WebQuery query, 
                                           string url, 
                                           out WebQueryAsyncResult result, 
                                           object userState)
        {
            var cache = GetCache(request);
            if (cache == null)
            {
                result = null;
                return false;
            }

            var options = GetCacheOptions(request);
            if (options == null)
            {
                result = null;
                return false;
            }

            // [DC]: This is currently prefixed to the full URL
            var function = GetCacheKeyFunction(request);
            var key = function != null ? function.Invoke() : "";

            switch (options.Mode)
            {
                case CacheMode.NoExpiration:
                    result = query.RequestAsync(url, key, cache, userState);
                    break;
                case CacheMode.AbsoluteExpiration:
                    var expiry = options.Duration.FromNow();
                    result = query.RequestAsync(url, key, cache, expiry, userState);
                    break;
                case CacheMode.SlidingExpiration:
                    result = query.RequestAsync(url, key, cache, options.Duration, userState);
                    break;
                default:
                    throw new NotSupportedException("Unknown CacheMode");
            }

            return true;
        }

        private RestResponse BuildResponseFromResult(RestRequest request, WebQuery query)
        {
            request = request ?? new RestRequest();
            var result = query.Result;
            var response = BuildBaseResponse(result);

            DeserializeEntityBody(result, request, response);
            response.Tag = GetTag(request);

            return response;
        }
        private RestResponse<T> BuildResponseFromResult<T>(RestBase request, WebQuery query)
        {
            request = request ?? new RestRequest();
            var result = query.Result;
            var response = BuildBaseResponse<T>(result);

            DeserializeEntityBody(result, request, response);
            response.Tag = GetTag(request);

            return response;
        }

        private static readonly Func<RestResponseBase, WebQueryResult, RestResponseBase> _baseSetter =
                (response, result) =>
                {
                    response.InnerResponse = result.WebResponse;
                    response.InnerException = result.Exception;
                    response.RequestDate = result.RequestDate;
                    response.RequestUri = result.RequestUri;
                    response.RequestMethod = result.RequestHttpMethod;
                    response.RequestKeptAlive = result.RequestKeptAlive;
                    response.ResponseDate = result.ResponseDate;
                    response.ResponseUri = result.ResponseUri;
                    response.StatusCode = (HttpStatusCode)result.ResponseHttpStatusCode;
                    response.StatusDescription = result.ResponseHttpStatusDescription;
                    response.Content = result.Response;
                    response.ContentType = result.ResponseType;
                    response.ContentLength = result.ResponseLength;
                    response.IsMock = result.IsMock;
                    response.TimedOut = result.TimedOut;
                    return response;
                };

        private static RestResponse BuildBaseResponse(WebQueryResult result)
        {
            var response = new RestResponse();

            _baseSetter.Invoke(response, result);

            return response;
        }

        private static RestResponse<T> BuildBaseResponse<T>(WebQueryResult result)
        {
            var response = new RestResponse<T>();

            _baseSetter.Invoke(response, result);

            return response;
        }

        private void DeserializeEntityBody(WebQueryResult result, RestRequest request, RestResponse response)
        {
            var deserializer = request.Deserializer ?? Deserializer;
            if (deserializer != null && !result.Response.IsNullOrBlank() && request.ResponseEntityType != null)
            {
                response.ContentEntity = deserializer.Deserialize(result.Response, request.ResponseEntityType);
            }
        }
        private void DeserializeEntityBody<T>(WebQueryResult result, RestBase request, RestResponse<T> response)
        {
            var deserializer = request.Deserializer ?? Deserializer;
            if (deserializer != null && !result.Response.IsNullOrBlank())
            {
                response.ContentEntity = deserializer.Deserialize<T>(result.Response);
            }
        }

        private void SetQueryMeta(RestRequest request, WebQuery query)
        {
            // [DC]: Trump duplicates by request over client over info values
            foreach (var parameter in Parameters)
            {
                if (query.Parameters[parameter.Name] != null)
                {
                    query.Parameters[parameter.Name].Value = parameter.Value;
                }
                else
                {
                    query.Parameters.Add(parameter);
                }
            }
            foreach (var parameter in request.Parameters)
            {
                if (query.Parameters[parameter.Name] != null)
                {
                    query.Parameters[parameter.Name].Value = parameter.Value;
                }
                else
                {
                    query.Parameters.Add(parameter);
                }
            }
            query.Headers.AddRange(Headers);
            query.Headers.AddRange(request.Headers);

            // [DC]: These properties are trumped by request over client
            query.UserAgent = GetUserAgent(request);
            query.Method = GetWebMethod(request);
            query.Proxy = GetProxy(request);
            query.RequestTimeout = GetTimeout(request);
            query.DecompressionMethods = request.DecompressionMethods | DecompressionMethods;
            query.PostContent = GetPostContent(request);

            SerializeEntityBody(query, request);
        }

        private void SerializeEntityBody(WebQuery query, RestRequest request)
        {
            var serializer = GetSerializer(request);
            if (serializer == null)
            {
                // No suitable serializer for entity
                return;
            }

            if (request.Entity == null || request.RequestEntityType == null)
            {
                // Not enough information to serialize
                return;
            }

            var entityBody = serializer.Serialize(request.Entity, request.RequestEntityType);
            query.Entity = !entityBody.IsNullOrBlank()
                               ? new WebEntity
                                     {
                                         Content = entityBody,
                                         ContentEncoding = serializer.ContentEncoding,
                                         ContentType = serializer.ContentType
                                     }
                               : null;
        }

        private WebEntity SerializeExpectEntity(RestRequest request)
        {
            var serializer = GetSerializer(request);
            if (serializer == null || request.ExpectEntity == null)
            {
                // No suitable serializer or entity
                return null;
            }

            var entityBody = serializer.Serialize(request.ExpectEntity, request.RequestEntityType);
            var entity = !entityBody.IsNullOrBlank()
                               ? new WebEntity
                               {
                                   Content = entityBody,
                                   ContentEncoding = serializer.ContentEncoding,
                                   ContentType = serializer.ContentType
                               } : null;
            return entity;
        }

        private WebQuery GetQueryFor(RestBase request, Uri uri)
        {
            var method = GetWebMethod(request);
            var credentials = GetWebCredentials(request);
            var info = GetInfo(request);

            // [DC]: UserAgent is set via Info
            // [DC]: Request credentials trump client credentials
            var query = credentials != null
                            ? credentials.GetQueryFor(uri.ToString(), request, info, method)
                            : new BasicAuthWebQuery(info);

#if SILVERLIGHT
            query.HasElevatedPermissions = HasElevatedPermissions;
#endif
            return query;
        }

        public void CancelAllRepeatingTasks()
        {
            lock (_timedTasksLock)
            {
                //copy to a new list, since canceling 
                //the task removes it from the _tasks
                //list, the enumeration will throw
                var toCancel = new List<TimedTask>();
                toCancel.AddRange(_tasks.Values);
                toCancel.ForEach(t => t.Stop());
            }
        }

    }
}