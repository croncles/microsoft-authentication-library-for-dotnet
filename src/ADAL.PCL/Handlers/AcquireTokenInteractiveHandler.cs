﻿//----------------------------------------------------------------------
// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
// Apache License 2.0
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.IdentityModel.Clients.ActiveDirectory
{
    internal class AcquireTokenInteractiveHandler : AcquireTokenHandlerBase
    {

        internal AuthorizationResult authorizationResult;

        private readonly HashSet<string> _additionalScope;
        private readonly Uri _redirectUri;
        private readonly string _redirectUriRequestParameter;
        private readonly IPlatformParameters _authorizationParameters;
        private readonly string _extraQueryParameters;
        private readonly IWebUI _webUi;
        private readonly string _loginHint;
        private readonly UiOptions _uiOptions;

        public AcquireTokenInteractiveHandler(Authenticator authenticator, TokenCache tokenCache, string[] scope,
            string[] additionalScope, string clientId, Uri redirectUri, IPlatformParameters parameters, string loginHint, UiOptions uiOptions, string extraQueryParameters, string policy, IWebUI webUI)
            : base(authenticator, tokenCache, scope, new ClientKey(clientId), policy, TokenSubjectType.User)
        {
            this._redirectUri = PlatformPlugin.PlatformInformation.ValidateRedirectUri(redirectUri, this.CallState);

            if (!string.IsNullOrWhiteSpace(this._redirectUri.Fragment))
            {
                throw new ArgumentException(MsalErrorMessage.RedirectUriContainsFragment, "redirectUri");
            }
            
            _additionalScope = new HashSet<string>();
            if (!MsalStringHelper.IsNullOrEmpty(additionalScope))
            {
                this._additionalScope = additionalScope.CreateSetFromArray();
            }

            ValidateScopeInput(this._additionalScope);
            this._authorizationParameters = parameters;
            this._redirectUriRequestParameter = PlatformPlugin.PlatformInformation.GetRedirectUriAsString(this._redirectUri, this.CallState);
            

            this._loginHint = loginHint;
            if (!string.IsNullOrEmpty(extraQueryParameters) && extraQueryParameters[0] == '&')
            {
                extraQueryParameters = extraQueryParameters.Substring(1);
            }

            this._extraQueryParameters = extraQueryParameters;
            this._webUi = webUI;
            this._uiOptions = uiOptions;
            this.LoadFromCache = tokenCache != null;
            this.SupportADFS = true;

            this.brokerParameters["force"] = "NO";
            this.brokerParameters["username"] = loginHint;
            this.brokerParameters["redirect_uri"] = redirectUri.AbsoluteUri;
            this.brokerParameters["extra_qp"] = extraQueryParameters;
            PlatformPlugin.BrokerHelper.PlatformParameters = _authorizationParameters;
        }

        protected override async Task PreTokenRequest()
        {
            await base.PreTokenRequest().ConfigureAwait(false);

            // We do not have async interactive API in .NET, so we call this synchronous method instead.
            await this.AcquireAuthorizationAsync().ConfigureAwait(false);
            this.VerifyAuthorizationResult();
        }

        internal async Task AcquireAuthorizationAsync()
        {
            Uri authorizationUri = this.CreateAuthorizationUri();
            this.authorizationResult = await this._webUi.AcquireAuthorizationAsync(authorizationUri, this._redirectUri, null, this.CallState).ConfigureAwait(false);
        }

        internal async Task<Uri> CreateAuthorizationUriAsync(Guid correlationId)
        {
            this.CallState.CorrelationId = correlationId;
            await this.Authenticator.UpdateFromTemplateAsync(this.CallState).ConfigureAwait(false);
            return this.CreateAuthorizationUri();
        }
        protected override void AddAditionalRequestParameters(DictionaryRequestParameters requestParameters)
        {
            requestParameters[OAuthParameter.GrantType] = OAuthGrantType.AuthorizationCode;
            requestParameters[OAuthParameter.Code] = this.authorizationResult.Code;
            requestParameters[OAuthParameter.RedirectUri] = this._redirectUriRequestParameter;
        }

        protected override void PostTokenRequest(AuthenticationResultEx resultEx)
        {
            base.PostTokenRequest(resultEx);
            //MSAL does not compare the input loginHint to the returned identifier anymore.
        }

        private Uri CreateAuthorizationUri()
        {
            IRequestParameters requestParameters = this.CreateAuthorizationRequest(_loginHint);
            return  new Uri(new Uri(this.Authenticator.AuthorizationUri), "?" + requestParameters);
        }

        private DictionaryRequestParameters CreateAuthorizationRequest(string loginHint)
        {
            var authorizationRequestParameters = new DictionaryRequestParameters(this.Scope, this.ClientKey);
            authorizationRequestParameters[OAuthParameter.ResponseType] = OAuthResponseType.Code;

            authorizationRequestParameters[OAuthParameter.RedirectUri] = this._redirectUriRequestParameter;

            if (!string.IsNullOrWhiteSpace(loginHint))
            {
                authorizationRequestParameters[OAuthParameter.LoginHint] = loginHint;
            }

            if (this.CallState != null && this.CallState.CorrelationId != Guid.Empty)
            {
                authorizationRequestParameters[OAuthParameter.CorrelationId] = this.CallState.CorrelationId.ToString();
            }
            
                IDictionary<string, string> adalIdParameters = MsalIdHelper.GetAdalIdParameters();
                foreach (KeyValuePair<string, string> kvp in adalIdParameters)
                {
                    authorizationRequestParameters[kvp.Key] = kvp.Value;
                }
        
            if (!string.IsNullOrWhiteSpace(_extraQueryParameters))
            {
                // Checks for _extraQueryParameters duplicating standard parameters
                Dictionary<string, string> kvps = EncodingHelper.ParseKeyValueList(_extraQueryParameters, '&', false, this.CallState);
                foreach (KeyValuePair<string, string> kvp in kvps)
                {
                    if (authorizationRequestParameters.ContainsKey(kvp.Key))
                    {
                        throw new MsalException(MsalError.DuplicateQueryParameter, string.Format(MsalErrorMessage.DuplicateQueryParameterTemplate, kvp.Key));
                    }
                }

                authorizationRequestParameters.ExtraQueryParameter = _extraQueryParameters;
            }

            return authorizationRequestParameters;
        }

        private void VerifyAuthorizationResult()
        {
            if (this.authorizationResult.Error == OAuthError.LoginRequired)
            {
                throw new MsalException(MsalError.UserInteractionRequired);
            }

            if (this.authorizationResult.Status != AuthorizationStatus.Success)
            {
                throw new MsalServiceException(this.authorizationResult.Error, this.authorizationResult.ErrorDescription);
            }
        }


        protected override void UpdateBrokerParameters(IDictionary<string, string> parameters)
        {
            Uri uri = new Uri(this.authorizationResult.Code);
            string query = EncodingHelper.UrlDecode(uri.Query);
            Dictionary<string, string> kvps = EncodingHelper.ParseKeyValueList(query, '&', false, this.CallState);
            parameters["username"] = kvps["username"];
        }

        protected override bool BrokerInvocationRequired()
        {
            if (this.authorizationResult != null
                && !string.IsNullOrEmpty(this.authorizationResult.Code)
                && this.authorizationResult.Code.StartsWith("msauth://"))
            {
                this.brokerParameters["broker_install_url"] = this.authorizationResult.Code;
                return true;
            }

            return false;
        }
    }
}
