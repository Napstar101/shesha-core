﻿using AutoMapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shesha.DynamicEntities.Dtos;
using Shesha.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shesha.DynamicEntities
{
    /// <summary>
    /// 
    /// </summary>
    public class DynamicDtoModelBinder : IModelBinder
    {
        private readonly IList<IInputFormatter> _formatters;
        private readonly Func<Stream, Encoding, TextReader> _readerFactory;
        private readonly ILogger _logger;
        private readonly MvcOptions? _options;
        private readonly IDynamicDtoTypeBuilder _dtoBuilder;

        /// <summary>
        /// Creates a new <see cref="DynamicDtoModelBinder"/>.
        /// </summary>
        /// <param name="formatters">The list of <see cref="IInputFormatter"/>.</param>
        /// <param name="readerFactory">
        /// The <see cref="IHttpRequestStreamReaderFactory"/>, used to create <see cref="System.IO.TextReader"/>
        /// instances for reading the request body.
        /// </param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
        public DynamicDtoModelBinder(
            IList<IInputFormatter> formatters,
            IHttpRequestStreamReaderFactory readerFactory,
            ILoggerFactory? loggerFactory)
            : this(formatters, readerFactory, loggerFactory, options: null)
        {
        }

        /// <summary>
        /// Creates a new <see cref="DynamicDtoModelBinder"/>.
        /// </summary>
        /// <param name="formatters">The list of <see cref="IInputFormatter"/>.</param>
        /// <param name="readerFactory">
        /// The <see cref="IHttpRequestStreamReaderFactory"/>, used to create <see cref="System.IO.TextReader"/>
        /// instances for reading the request body.
        /// </param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
        /// <param name="options">The <see cref="MvcOptions"/>.</param>
        public DynamicDtoModelBinder(
            IList<IInputFormatter> formatters,
            IHttpRequestStreamReaderFactory readerFactory,
            ILoggerFactory? loggerFactory,
            MvcOptions? options)
        {
            if (formatters == null)
            {
                throw new ArgumentNullException(nameof(formatters));
            }

            if (readerFactory == null)
            {
                throw new ArgumentNullException(nameof(readerFactory));
            }

            _formatters = formatters;
            _readerFactory = readerFactory.CreateReader;

            _logger = loggerFactory?.CreateLogger<DynamicDtoModelBinder>() ?? NullLogger<DynamicDtoModelBinder>.Instance;

            _options = options;
            _dtoBuilder = StaticContext.IocManager.Resolve<IDynamicDtoTypeBuilder>();
        }

        internal bool AllowEmptyBody { get; set; }

        /// <inheritdoc />
        public async Task BindModelAsync(ModelBindingContext bindingContext)
        {
            if (bindingContext == null)
            {
                throw new ArgumentNullException(nameof(bindingContext));
            }

            _logger.AttemptingToBindModel(bindingContext);

            // Special logic for body, treat the model name as string.Empty for the top level
            // object, but allow an override via BinderModelName. The purpose of this is to try
            // and be similar to the behavior for POCOs bound via traditional model binding.
            string modelBindingKey;
            if (bindingContext.IsTopLevelObject)
            {
                modelBindingKey = bindingContext.BinderModelName ?? string.Empty;
            }
            else
            {
                modelBindingKey = bindingContext.ModelName;
            }

            var httpContext = bindingContext.HttpContext;

            #region 

            // check if type is already proxied
            var modelType = bindingContext.ModelType;
            var metadata = bindingContext.ModelMetadata;

            if (!(modelType is IDynamicDtoProxy))
            {
                modelType = await _dtoBuilder.BuildDtoProxyTypeAsync(bindingContext.ModelType);
                metadata = bindingContext.ModelMetadata.GetMetadataForType(modelType);
            }
            
            #endregion

            var formatterContext = new InputFormatterContext(
                httpContext,
                modelBindingKey,
                bindingContext.ModelState,
                metadata,
                _readerFactory,
                AllowEmptyBody);

            var formatter = (IInputFormatter?)null;
            for (var i = 0; i < _formatters.Count; i++)
            {
                if (_formatters[i].CanRead(formatterContext))
                {
                    formatter = _formatters[i];
                    _logger.InputFormatterSelected(formatter, formatterContext);
                    break;
                }
                else
                {
                    _logger.InputFormatterRejected(_formatters[i], formatterContext);
                }
            }

            if (formatter == null)
            {
                if (AllowEmptyBody)
                {
                    var hasBody = httpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody;
                    hasBody ??= httpContext.Request.ContentLength is not null && httpContext.Request.ContentLength == 0;
                    if (hasBody == false)
                    {
                        bindingContext.Result = ModelBindingResult.Success(model: null);
                        return;
                    }
                }

                _logger.NoInputFormatterSelected(formatterContext);

                var message = $"Unsupported content type '{httpContext.Request.ContentType}'.";
                var exception = new UnsupportedContentTypeException(message);
                bindingContext.ModelState.AddModelError(modelBindingKey, exception, bindingContext.ModelMetadata);
                _logger.DoneAttemptingToBindModel(bindingContext);
                return;
            }

            try
            {
                var result = await formatter.ReadAsync(formatterContext);

                if (result.HasError)
                {
                    // Formatter encountered an error. Do not use the model it returned.
                    _logger.DoneAttemptingToBindModel(bindingContext);
                    return;
                }

                if (result.IsModelSet)
                {
                    var model = result.Model;
                    bindingContext.Result = ModelBindingResult.Success(model);

                    // map results
                    if (bindingContext.Model != null && result.Model != null) 
                    {
                        var mapper = GetMapper(result.Model.GetType(), bindingContext.Model.GetType());
                        mapper.Map(result.Model, bindingContext.Model);
                    }
                }
                else
                {
                    // If the input formatter gives a "no value" result, that's always a model state error,
                    // because BodyModelBinder implicitly regards input as being required for model binding.
                    // If instead the input formatter wants to treat the input as optional, it must do so by
                    // returning InputFormatterResult.Success(defaultForModelType), because input formatters
                    // are responsible for choosing a default value for the model type.
                    var message = bindingContext
                        .ModelMetadata
                        .ModelBindingMessageProvider
                        .MissingRequestBodyRequiredValueAccessor();
                    bindingContext.ModelState.AddModelError(modelBindingKey, message);
                }
            }
            catch (Exception exception) when (exception is InputFormatterException || ShouldHandleException(formatter))
            {
                bindingContext.ModelState.AddModelError(modelBindingKey, exception, bindingContext.ModelMetadata);
            }

            _logger.DoneAttemptingToBindModel(bindingContext);
        }

        private IMapper GetMapper(Type sourceType, Type destinationType)
        {
            var modelConfigMapperConfig = new MapperConfiguration(cfg => {
                var mapExpression = cfg.CreateMap(sourceType, destinationType);
                //.ForMember(d => d.Id, o => o.Ignore());
            });

            return modelConfigMapperConfig.CreateMapper();
        }


        private bool ShouldHandleException(IInputFormatter formatter)
        {
            // Any explicit policy on the formatters overrides the default.
            var policy = (formatter as IInputFormatterExceptionPolicy)?.ExceptionPolicy ??
                InputFormatterExceptionPolicy.MalformedInputExceptions;

            return policy == InputFormatterExceptionPolicy.AllExceptions;
        }
    }
}
