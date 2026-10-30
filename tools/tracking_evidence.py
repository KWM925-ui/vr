"""Independently check the ordered Update/LateUpdate tracking clock evidence."""
import math


def require(condition, message):
    if not condition:
        raise ValueError('tracking evidence: ' + message)


def verify_observations(rows):
    require(isinstance(rows, list) and rows, 'observations missing')
    last_submit = None
    last_observer = -1
    counts = dict(submitted=0, applied=0, stale=0, recovered=0)
    for row in rows:
        require(not row.get('failureReason'), 'recorded failure: ' + str(row.get('failureReason')))
        now = row['observerTimeSeconds']
        require(math.isfinite(now) and now >= last_observer, 'observer time reversed')
        last_observer = now
        if row['kind'] == 'submission':
            submitted = row['attemptedSubmitTimeSeconds']
            require(row['submissionAccepted'] is True and math.isfinite(submitted)
                    and submitted <= now and (last_submit is None or submitted > last_submit), 'invalid submission')
            last_submit = submitted
            counts['submitted'] += 1
            continue
        require(row['kind'] == 'late_update', 'unknown record kind')
        if last_submit is None:
            require(not row['hasSubmitted'] and not row['poseApplied'], 'unobserved input applied')
            continue
        evaluated = row['evaluationTimeSeconds']
        require(row['hasSubmitted'] and row['evaluationClockValid']
                and row['evaluationFrame'] == row['frame'] == row['updateFrame'], 'missing current-frame evaluation')
        require(math.isfinite(evaluated) and row['updateTimeSeconds'] <= evaluated <= now,
                'evaluation outside its lifecycle interval')
        require(row['lastSubmitTimeSeconds'] == last_submit, 'evaluation uses an unobserved submission')
        age = evaluated - last_submit
        require(row['reportedAgeFinite'] and row['independentAgeValid'] and age >= 0
                and abs(row['reportedAgeSeconds']-age) <= 1e-9
                and abs(row['independentAgeSeconds']-age) <= 1e-9, 'age differs from submission/evaluation clocks')
        require(abs(row['maximumTrackingAgeSeconds']-.1) < 1e-8, '100 ms TTL changed')
        if age > row['maximumTrackingAgeSeconds']:
            require(not row['poseApplied'] and row['poseStatus']=='TRACKING_STALE_OR_INVALID_CLOCK', 'expired pose applied')
            if not row['feedTracking']: counts['stale'] += 1
        else:
            require(row['poseApplied'] and row['poseChecked'], 'fresh pose missing')
            require(0 <= row['positionErrorM'] < 1e-4 and 0 <= row['rotationErrorDeg'] < .02, 'pose error exceeds original limit')
            counts['applied'] += 1
            if row['recoveredAfterExpiry']: counts['recovered'] += 1
    return counts


def verify_runtime_tracking(report):
    counts = verify_observations(report.get('trackingObservations'))
    for key, name in [('submitted','trackingSubmittedFrames'),('applied','trackingAppliedFrames'),
                      ('stale','trackingStaleFrames'),('recovered','trackingRecoveredAppliedFrames')]:
        require(counts[key] == report[name], 'counter mismatch: ' + name)
    require(counts['applied'] >= 100 and counts['stale'] >= 20 and counts['recovered'] >= 100,
            'insufficient applied/expired/recovered observations')
    return counts
