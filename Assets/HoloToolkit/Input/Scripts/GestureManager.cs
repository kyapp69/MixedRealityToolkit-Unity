﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VR.WSA.Input;

namespace HoloToolkit.Unity
{
    /// <summary>
    /// GestureManager provides access to several different input gestures, including Tap and Manipulation.
    /// </summary>
    /// <remarks>
    /// When a tap gesture is detected, GestureManager uses GazeManager to find the currently focused object.
    /// GestureManager then sends a message to that game object.
    /// 
    /// Using Manipulation requires subscribing the the ManipulationStarted events and then querying
    /// information about the manipulation gesture via ManipulationOffset and ManipulationHandPosition.
    /// 
    /// Editor and Companion App Input can also be used by assigning a keyboard select key and
    /// using both left and right mouse buttons to select the currently focused object. 
    /// </remarks>
    [RequireComponent(typeof(GazeManager))]
    public partial class GestureManager : Singleton<GestureManager>
    {
        #region Delegate Events

        /// <summary>
        /// Occurs when a manipulation gesture has started.
        /// </summary>
        /// <param name="sourceKind">The Interaction Source Kind that started the event.</param>
        public delegate void ManipulationStartedDelegate(InteractionSourceKind sourceKind);
        public event ManipulationStartedDelegate OnManipulationStarted;

        /// <summary>
        /// Occurs when a manipulation gesture ended as a result of user input.
        /// </summary>
        /// <param name="sourceKind">The Interaction Source Kind that completed the event.</param>
        public delegate void ManipulationCompletedDelegate(InteractionSourceKind sourceKind);
        public event ManipulationCompletedDelegate OnManipulationCompleted;

        /// <summary>
        /// Occurs when a manipulated gesture ended as a result of some other condition.
        /// (e.g. the hand being used for the gesture is no longer visible).
        /// </summary>
        /// <param name="sourceKind">The Interaction Source Kind that cancelled the event.</param>
        public delegate void ManipulationCanceledDelegate(InteractionSourceKind sourceKind);
        public event ManipulationCanceledDelegate OnManipulationCanceled;

        #endregion

        #region Public Properties

        /// <summary>
        /// To select even when a hologram is not being gazed at,
        /// set the override focused object.
        /// If its null, then the gazed at object will be selected.
        /// </summary>
        public GameObject OverrideFocusedObject { get; set; }

        /// <summary>
        /// Gets the currently focused object, or null if none.
        /// </summary>
        public GameObject FocusedObject { get; private set; }

        /// <summary>
        /// Whether or not a manipulation gesture is currently in progress
        /// </summary>
        public bool ManipulationInProgress { get; private set; }

        /// <summary>
        /// The offset of the hand from its position at the beginning of 
        /// the currently active manipulation gesture, in world space.  Not valid if
        /// a manipulation gesture is not in progress
        /// </summary>
        public Vector3 ManipulationOffset { get; private set; }

        /// <summary>
        /// The world space position of manipulation source being used for the current manipulation gesture.
        /// Valid only if a manipulation gesture is in progress.
        /// </summary>
        public Vector3 ManipulationPosition
        {
            get
            {
                Vector3 position;
                if (!currentInteractionSourceState.properties.location.TryGetPosition(out position))
                {
                    position = Vector3.zero;
                }
                return position;
            }
        }

        /// <summary>
        /// InteractionSourceDetected tracks the interaction detected state.
        /// Returns true if the list of tracked interactions is not empty.
        /// </summary>
        public bool InteractionSourceDetected
        {
            get { return trackedInteractionSource.Count > 0; }
        }

        #endregion

#if UNITY_EDITOR || UNITY_STANDALONE
        /// <summary>
        /// Key to press that will select the currently focused object.
        /// </summary>
        public KeyCode keyboardSelectKey = KeyCode.Space;
#endif

        private GestureRecognizer gestureRecognizer;

        /// <summary> We use a separate manipulation recognizer here because the tap gesture recognizer cancels
        /// capturing gestures whenever the GazeManager focus changes, which is not the behavior
        /// we want for manipulation
        /// </summary>
        private GestureRecognizer manipulationRecognizer;

        private InteractionSourceState currentInteractionSourceState;

        private HashSet<uint> trackedInteractionSource = new HashSet<uint>();

        private HashSet<uint> pressedInteractionSource = new HashSet<uint>();

        private bool hasRecognitionStarted;

        private GameObject lastFocusedObject;

        private void Awake()
        {

            InteractionManager.SourceDetected += InteractionManager_SourceDetected;
            InteractionManager.SourcePressed += InteractionManager_SourcePressed;
            InteractionManager.SourceReleased += InteractionManager_SourceReleased;
            InteractionManager.SourceUpdated += InteractionManager_SourceUpdated;
            InteractionManager.SourceLost += InteractionManager_SourceLost;

            // Create a new GestureRecognizer. Sign up for tapped events.
            gestureRecognizer = new GestureRecognizer();
            gestureRecognizer.SetRecognizableGestures(GestureSettings.Tap);

            manipulationRecognizer = new GestureRecognizer();
            manipulationRecognizer.SetRecognizableGestures(GestureSettings.ManipulationTranslate);

            gestureRecognizer.TappedEvent += GestureRecognizer_TappedEvent;

            // We need to send pressed and released events to UI so they can provide visual feedback
            // of the current state of the UI based on user input.
            gestureRecognizer.RecognitionStartedEvent += GestureRecognizer_RecognitionStartedEvent;
            gestureRecognizer.RecognitionEndedEvent += GestureRecogniser_RecognitionEndedEvent;

            manipulationRecognizer.ManipulationStartedEvent += ManipulationRecognizer_ManipulationStartedEvent;
            manipulationRecognizer.ManipulationUpdatedEvent += ManipulationRecognizer_ManipulationUpdatedEvent;
            manipulationRecognizer.ManipulationCompletedEvent += ManipulationRecognizer_ManipulationCompletedEvent;
            manipulationRecognizer.ManipulationCanceledEvent += ManipulationRecognizer_ManipulationCanceledEvent;

            // Start looking for gestures.
            gestureRecognizer.StartCapturingGestures();
            manipulationRecognizer.StartCapturingGestures();
        }

        #region Interaction Management

        /// <summary>
        /// Thrown when we detect an interaction source.
        /// </summary>
        /// <param name="state"></param>
        private void InteractionManager_SourceDetected(InteractionSourceState state)
        {
            trackedInteractionSource.Add(state.source.id);
        }

        /// <summary>
        /// Thrown when the interaction source is pressed.
        /// </summary>
        /// <param name="state">The current state of the Interaction source.</param>
        private void InteractionManager_SourcePressed(InteractionSourceState state)
        {
            if (!InteractionSourceDetected)
            {
                currentInteractionSourceState = state;
            }

            pressedInteractionSource.Add(state.source.id);
        }

        /// <summary>
        /// Thrown when the interaction source is updated.
        /// </summary>
        /// <param name="state">The current state of the Interaction source.</param>
        private void InteractionManager_SourceUpdated(InteractionSourceState state)
        {
            if (InteractionSourceDetected && state.source.id == currentInteractionSourceState.source.id)
            {
                currentInteractionSourceState = state;
            }
        }

        /// <summary>
        /// Thrown when the interaction source is released.
        /// </summary>
        /// <param name="state">The current state of the Interaction source.</param>
        private void InteractionManager_SourceReleased(InteractionSourceState state)
        {
            pressedInteractionSource.Remove(state.source.id);
        }

        /// <summary>
        /// Thrown when the interaction source is no longer availible.
        /// </summary>
        /// <param name="state">The current state of the Interaction source.</param>
        private void InteractionManager_SourceLost(InteractionSourceState state)
        {
            trackedInteractionSource.Remove(state.source.id);
        }

        #endregion

        #region Gesture Management

        /// <summary>
        /// Throws <see cref="OnTap"/>.
        /// </summary>
        /// <param name="source">Interaction Source.</param>
        /// <param name="tapCount">TODO: Need clarification on what this is </param>
        /// <param name="headRay">The Ray from the users forward direction.</param>
        private void GestureRecognizer_TappedEvent(InteractionSourceKind source, int tapCount, Ray headRay)
        {
            CalcFocusedObject();
            OnTap();
        }

        /// <summary>
        /// Throws <see cref="OnRecognitionStarted"/>. Only used for UI states.
        /// </summary>
        /// <param name="source">Input Source.</param>
        /// <param name="headRay">The Ray from the users forward direction.</param>
        private void GestureRecognizer_RecognitionStartedEvent(InteractionSourceKind source, Ray headRay)
        {
            CalcFocusedObject();
            OnRecognitionStarted();
        }

        /// <summary>
        /// Throws <see cref="OnRecognitionEndeded"/>. Only used for UI states.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="headRay"></param>
        private void GestureRecogniser_RecognitionEndedEvent(InteractionSourceKind source, Ray headRay)
        {
            OnRecognitionEndeded(CalcFocusedObject());
        }

        #endregion

        #region Manipulation Management

        /// <summary>
        /// Thrown when the gesture manager recognizes that a manipulation has begun.
        /// </summary>
        /// <param name="source">Input Source Kind.</param>
        /// <param name="cumulativeDelta">Cumlulative Data.</param>
        /// <param name="headRay">The Ray from the users forward direction.</param>
        private void ManipulationRecognizer_ManipulationStartedEvent(InteractionSourceKind source, Vector3 cumulativeDelta, Ray headRay)
        {
            // Don't start another manipulation gesture if one is already underway
            if (!ManipulationInProgress)
            {
                OnManipulation(inProgress: true, offset: cumulativeDelta);
                if (OnManipulationStarted != null)
                {
                    OnManipulationStarted(source);
                }
            }
        }

        /// <summary>
        /// Thrown when the gesture manager recognizes that a manipulation has been updated.
        /// </summary>
        /// <param name="source">Input Source Kind.</param>
        /// <param name="cumulativeDelta">Cumlulative Data.</param>
        /// <param name="headRay">The Ray from the users forward direction.</param>
        private void ManipulationRecognizer_ManipulationUpdatedEvent(InteractionSourceKind source, Vector3 cumulativeDelta, Ray headRay)
        {
            OnManipulation(inProgress: true, offset: cumulativeDelta);
        }

        /// <summary>
        /// Thrown when the gesture manager recognizes that a manipulation has completed.
        /// </summary>
        /// <param name="source">Input Source Kind.</param>
        /// <param name="cumulativeDelta">Cumlulative Data.</param>
        /// <param name="headRay">The Ray from the users forward direction.</param>
        private void ManipulationRecognizer_ManipulationCompletedEvent(InteractionSourceKind source, Vector3 cumulativeDelta, Ray headRay)
        {
            OnManipulation(inProgress: false, offset: cumulativeDelta);
            if (OnManipulationCompleted != null)
            {
                OnManipulationCompleted(source);
            }
        }

        /// <summary>
        /// Thrown when the gesture manager recognizes that a manipulation has been canceled.
        /// </summary>
        /// <param name="source">Input Source Kind.</param>
        /// <param name="cumulativeDelta">Cumlulative Data.</param>
        /// <param name="headRay">The Ray from the users forward direction.</param>
        private void ManipulationRecognizer_ManipulationCanceledEvent(InteractionSourceKind source, Vector3 cumulativeDelta, Ray headRay)
        {
            OnManipulation(inProgress: false, offset: cumulativeDelta);
            if (OnManipulationCanceled != null)
            {
                OnManipulationCanceled(source);
            }
        }

        /// <summary>
        /// Processes Manipulation Data.
        /// </summary>
        /// <param name="inProgress">Is this manipulation in progress?</param>
        /// <param name="offset">The Offset of our manipulation to calulate delta positions.</param>
        private void OnManipulation(bool inProgress, Vector3 offset)
        {
            ManipulationInProgress = inProgress;
            ManipulationOffset = offset;
        }

        #endregion

        #region Event Management

        /// <summary>
        /// Throws OnSelect.
        /// </summary>
        private void OnTap()
        {
            if (FocusedObject != null)
            {
                FocusedObject.SendMessage("OnSelect", SendMessageOptions.DontRequireReceiver);
            }
        }

        /// <summary>
        /// Throws OnPressed.  Only used for determining UI states.
        /// </summary>
        private void OnRecognitionStarted()
        {
            if (FocusedObject != null)
            {
                hasRecognitionStarted = true;
                FocusedObject.SendMessage("OnPressed", SendMessageOptions.DontRequireReceiver);
            }
        }

        /// <summary>
        /// Throws OnReleased. Only used for determining UI states.
        /// </summary>
        private void OnRecognitionEndeded(bool changedFocus)
        {
            GameObject focusedObject = FocusedObject;
            if (changedFocus)
            {
                focusedObject = lastFocusedObject;
            }

            if (focusedObject != null && hasRecognitionStarted)
            {
                focusedObject.SendMessage("OnReleased", SendMessageOptions.DontRequireReceiver);
            }

            hasRecognitionStarted = false;
        }

        #endregion

        /// <summary>
        /// Calculates the current object in Focus.
        /// </summary>
        /// <returns>True if we've changed our focus to a new object, else false.</returns>
        private bool CalcFocusedObject()
        {
            // set the next focus object to see if focus has changed, but don't replace the current focused object
            // until all the inputs are handled, like Unity Editor input for OnTap().
            GameObject newFocusedObject;

            if (GazeManager.Instance.Hit &&
                OverrideFocusedObject == null &&
                GazeManager.Instance.HitInfo.collider != null)
            {
                // If gaze hits a hologram, set the focused object to that game object.
                // Also if the caller has not decided to override the focused object.
                newFocusedObject = GazeManager.Instance.HitInfo.collider.gameObject;
            }
            else
            {
                // If our gaze doesn't hit a hologram, set the focused object to null or override focused object.
                newFocusedObject = OverrideFocusedObject;
            }

            // Checks to see if our focus has changed.
            bool focusedChanged = FocusedObject != newFocusedObject;

            if (focusedChanged)
            {
                // If the currently focused object doesn't match the new focused object, cancel the current gesture.
                // This is to prevent applying gestures from one hologram to another.
                gestureRecognizer.CancelGestures();

                // Set our last Focused object.
                lastFocusedObject = FocusedObject;

                // Set our current Focused Object.
                FocusedObject = newFocusedObject;

                // Start looking for new gestures.
                gestureRecognizer.StartCapturingGestures();
            }

            return focusedChanged;
        }

        private void LateUpdate()
        {
            bool focusedChanged = CalcFocusedObject();

#if UNITY_EDITOR || UNITY_STANDALONE
            // Process Editor/Companion app input.  Tap by pressing both right and left mouse buttons.  Release Tap is on any mouse button up.

            // If we're already pressing a button, our keyboard select key, or if the focus has changed then throw recognition Ended.
            if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetKeyUp(keyboardSelectKey) || focusedChanged)
            {
                OnRecognitionEndeded(focusedChanged);
            }

            // If we're currently pressing both mouse buttons, or our keyboard select key.
            if ((Input.GetMouseButtonDown(0) && Input.GetMouseButtonDown(1)) || Input.GetKeyDown(keyboardSelectKey))
            {
                // If our focus has changed or we're not currenly manipulating our object in focus since the last frame,
                // then throw a new Tap and start recognition.
                if (focusedChanged || !ManipulationInProgress)
                {
                    OnRecognitionStarted();
                    OnTap();
                }
            }
#endif
        }

        private void OnDestroy()
        {
            gestureRecognizer.StopCapturingGestures();
            gestureRecognizer.TappedEvent -= GestureRecognizer_TappedEvent;
            gestureRecognizer.RecognitionStartedEvent -= GestureRecognizer_RecognitionStartedEvent;
            gestureRecognizer.RecognitionEndedEvent -= GestureRecogniser_RecognitionEndedEvent;

            manipulationRecognizer.StopCapturingGestures();
            manipulationRecognizer.ManipulationStartedEvent -= ManipulationRecognizer_ManipulationStartedEvent;
            manipulationRecognizer.ManipulationUpdatedEvent -= ManipulationRecognizer_ManipulationUpdatedEvent;
            manipulationRecognizer.ManipulationCompletedEvent -= ManipulationRecognizer_ManipulationCompletedEvent;
            manipulationRecognizer.ManipulationCanceledEvent -= ManipulationRecognizer_ManipulationCanceledEvent;

            InteractionManager.SourceDetected -= InteractionManager_SourceDetected;
            InteractionManager.SourcePressed -= InteractionManager_SourcePressed;
            InteractionManager.SourceReleased -= InteractionManager_SourceReleased;
            InteractionManager.SourceUpdated -= InteractionManager_SourceUpdated;
            InteractionManager.SourceLost -= InteractionManager_SourceLost;
        }
    }
}