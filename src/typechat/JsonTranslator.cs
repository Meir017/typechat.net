﻿// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.TypeChat;

/// <summary>
/// JsonTranslator translates natural language requests into objects of type T.
/// 
/// Translation works as follows:
/// - The language model - which works in text - translates the request into JSON. JsonTranslator gives the model the schema describing the
/// structure of the JSON it should emit.
/// - This schema is typically expressed using the Typescript language, which was designed to define schema consisely.
/// - JsonTranslator can automatically generate Typescript schema for T. But you can also create schema in other ways.
/// - The model returns JSON.
/// - JsonTranslator uses Validators to validate and deserialize the returned JSON into a valid object of type T
/// - Optional ConstraintsValidators can also further valdidate the object
/// 
/// Since language models are stochastic, the returned JSON can have errors or fail type checks.
/// When this happens, JsonTranslator tries to REPAIR the JSON by sending translation errors back to the language model.
/// JsonTranslator will attempt repairs MaxRepairAttempts number of times.
/// 
/// </summary>
/// <typeparam name="T">Type to translate requests into</typeparam>
public class JsonTranslator<T> : IJsonTranslator
{
    public const int DefaultMaxRepairAttempts = 1;

    int _maxRepairAttempts = DefaultMaxRepairAttempts;

    /// <summary>
    /// Creates a new JsonTranslator that translates natural language requests into objects of type T
    /// </summary>
    /// <param name="model">The language model to use for translation</param>
    /// <param name="schema">Text schema for type T</param>
    public JsonTranslator(ILanguageModel model, SchemaText schema)
        : this(model, new JsonSerializerTypeValidator<T>(schema))
    {
    }

    /// <summary>
    /// Creates a new JsonTranslator that translates natural language requests into objects of type T
    /// </summary>
    /// <param name="model">The language model to use for translation</param>
    /// <param name="validator">Type validator to use to ensure that JSON returned by LLM can be transformed into T</param>
    /// <param name="prompts">(Optional) Customize Typechat prompts</param>
    public JsonTranslator(
        ILanguageModel model,
        IJsonTypeValidator<T> validator,
        IJsonTranslatorPrompts? prompts = null)
    {
        ArgumentVerify.ThrowIfNull(model, nameof(model));
        ArgumentVerify.ThrowIfNull(validator, nameof(validator));

        Model = model;
        Validator = validator;
        Prompts = prompts ?? JsonTranslatorPrompts.Default;
    }

    /// <summary>
    /// Create a new, customized JsonTranslator
    /// </summary>
    /// <param name="model">The language model to use for translation</param>
    /// <param name="prompts">Custom prompts to use during translation</param>
    /// <param name="knownVocabs">Any known vocabularies. JsonVocab attributes can bind to these during JSON deserialiation</param>
    public JsonTranslator(
        ILanguageModel model,
        IJsonTranslatorPrompts? prompts = null,
        IVocabCollection? knownVocabs = null)
        : this(model, new TypeValidator<T>(knownVocabs), prompts)
    {
    }

    /// <summary>
    /// The language model doing the translation
    /// </summary>
    public ILanguageModel Model { get; }

    /// <summary>
    /// The associated Json validator
    /// </summary>
    public IJsonTypeValidator<T> Validator { get; }

    /// <summary>
    /// Optional constraints validation, once a valid object of type T is available
    /// </summary>
    public IConstraintsValidator<T>? ConstraintsValidator { get; set; }

    /// <summary>
    /// Prompts used during translation
    /// </summary>
    public IJsonTranslatorPrompts Prompts { get; }

    /// <summary>
    /// Translation settings. Use this to customize attributes like MaxTokens emitted
    /// </summary>
    public TranslationSettings TranslationSettings { get; } = new();

    /// <summary>
    /// When > 0, JsonValidator will attempt to repair Json objects that fail to validate.
    /// By default, will make at least 1 attempt
    /// </summary>
    public int MaxRepairAttempts
    {
        get => _maxRepairAttempts;
        set => _maxRepairAttempts = Math.Max(value, 0);
    }

    // 
    // Subscribe to diagnostic and progress Events
    //

    /// <summary>
    /// Sending a prompt to the model
    /// </summary>
    public event Action<Prompt> SendingPrompt;

    /// <summary>
    /// Raw response from the model
    /// </summary>
    public event Action<string> CompletionReceived;

    /// <summary>
    /// Attempting repair with the given validation errors
    /// </summary>
    public event Action<string> AttemptingRepair;

    /// <summary>
    /// Translate a natural language request into an object'
    /// </summary>
    /// <param name="request">text request</param>
    /// <param name="cancellationToken">optional cancel token</param>
    /// <returns></returns>
    public async Task<object> TranslateToObjectAsync(string request, CancellationToken cancellationToken = default)
    {
        return await TranslateAsync(request, cancellationToken);
    }

    /// <summary>
    /// Translate a natural language request into an object of type 'T'
    /// </summary>
    /// <param name="request">text request</param>
    /// <param name="cancellationToken">optional cancel token</param>
    /// <returns>Result containing object of type T</returns>
    /// <exception cref="TypeChatException"></exception>
    public Task<T> TranslateAsync(string request, CancellationToken cancellationToken = default)
        => TranslateAsync(request, null, null, cancellationToken);

    /// <summary>
    /// Translate a natural language request into an object of type 'T'
    /// </summary>
    /// <param name="request"></param>
    /// <param name="preamble"></param>
    /// <param name="requestSettings"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result containing object of type T</returns>
    /// <exception cref="TypeChatException"></exception>
    public async Task<T> TranslateAsync(
        Prompt request,
        IList<IPromptSection>? preamble,
        TranslationSettings? requestSettings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentVerify.ThrowIfNull(request, nameof(request));

        requestSettings ??= TranslationSettings;
        Prompt prompt = CreateRequestPrompt(request, preamble);
        int repairAttempts = 0;
        while (true)
        {
            string responseText = await GetResponseAsync(prompt, requestSettings, cancellationToken).ConfigureAwait(false);

            JsonResponse jsonResponse = JsonResponse.Parse(responseText);
            Result<T> validationResult;
            if (jsonResponse.HasCompleteJson)
            {
                validationResult = ValidateJson(jsonResponse.Json);
                if (validationResult.Success)
                {
                    return validationResult;
                }
            }
            else if (jsonResponse.HasJson)
            {
                // Partial json
                validationResult = Result<T>.Error(TypeChatException.IncompleteJson(jsonResponse));
            }
            else
            {
                validationResult = Result<T>.Error(TypeChatException.NoJson(jsonResponse));
            }

            // Attempt to repair the Json that was returned
            ++repairAttempts;
            if (repairAttempts > _maxRepairAttempts)
            {
                TypeChatException.ThrowJsonValidation(request, jsonResponse, validationResult.Message);
            }
            NotifyEvent(AttemptingRepair, validationResult.Message);

            PromptSection repairPrompt = CreateRepairPrompt(responseText, validationResult);
            if (repairAttempts > 1)
            {
                // Remove the previous attempts
                prompt.Trim(2);
            }
            prompt.AppendResponse(responseText);
            prompt.Append(repairPrompt);
        }
    }

    protected virtual Prompt CreateRequestPrompt(Prompt request, IList<IPromptSection> preamble)
    {
        return Prompts.CreateRequestPrompt(Validator.Schema, request, preamble);
    }

    protected virtual async Task<string> GetResponseAsync(Prompt prompt, TranslationSettings requestSettings, CancellationToken cancellationToken)
    {
        NotifyEvent(SendingPrompt, prompt);
        string responseText = await Model.CompleteAsync(prompt, requestSettings, cancellationToken).ConfigureAwait(false);
        NotifyEvent(CompletionReceived, responseText);
        return responseText;
    }

    protected virtual PromptSection CreateRepairPrompt(string responseText, Result<T> validationResult)
        => Prompts.CreateRepairPrompt(Validator.Schema, responseText, validationResult.Message);

    // Return false if translation loop should stop
    protected virtual bool OnValidationComplete(Result<T> validationResult) => true;

    Result<T> ValidateJson(string json)
    {
        var result = Validator.Validate(json);
        if (!OnValidationComplete(result))
        {
            return result;
        }
        if (result.Success)
        {
            result = (ConstraintsValidator is not null) ?
                     ConstraintsValidator.Validate(result.Value) :
                     result;
        }
        return result;
    }

    protected void NotifyEvent(Action<Prompt> evt, Prompt prompt)
    {
        if (evt is not null)
        {
            try
            {
                evt(prompt);
            }
            catch { }
        }
    }

    protected void NotifyEvent(Action<string> evt, string value)
    {
        if (evt is not null)
        {
            try
            {
                evt(value);
            }
            catch { }
        }
    }
}
