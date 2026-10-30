import copy
import unittest
from tracking_evidence import verify_observations


def records():
    return [dict(kind='submission',observerTimeSeconds=1.0,attemptedSubmitTimeSeconds=1.0,
                 submissionAccepted=True),
            dict(kind='late_update',frame=10,updateFrame=10,evaluationFrame=10,hasSubmitted=True,
                 evaluationClockValid=True,updateTimeSeconds=1.,evaluationTimeSeconds=1.099,
                 observerTimeSeconds=1.101,lastSubmitTimeSeconds=1.,reportedAgeFinite=True,
                 independentAgeValid=True,reportedAgeSeconds=.099,independentAgeSeconds=.099,
                 maximumTrackingAgeSeconds=.1,poseApplied=True,poseChecked=True,
                 positionErrorM=0.,rotationErrorDeg=0.,recoveredAfterExpiry=False,feedTracking=False)]


class TrackingEvidenceTests(unittest.TestCase):
    def test_expiry_crossed_after_evaluation_does_not_retroactively_invalidate_pose(self):
        self.assertEqual(verify_observations(records())['applied'], 1)

    def test_actually_expired_application_is_rejected(self):
        rows=records(); row=rows[-1]
        row.update(evaluationTimeSeconds=1.101,observerTimeSeconds=1.102,
                   reportedAgeSeconds=.101,independentAgeSeconds=.101)
        with self.assertRaisesRegex(ValueError, 'expired pose'): verify_observations(rows)
        row.update(poseApplied=False,poseStatus='TRACKING_STALE_OR_INVALID_CLOCK')
        self.assertEqual(verify_observations(rows)['stale'],1)

    def test_old_missing_or_forged_evaluation_is_rejected(self):
        for update in [dict(evaluationFrame=9),dict(evaluationTimeSeconds=.9),
                       dict(evaluationTimeSeconds=1.2),dict(reportedAgeSeconds=.09),
                       dict(lastSubmitTimeSeconds=1.001),dict(maximumTrackingAgeSeconds=.2)]:
            rows=copy.deepcopy(records()); rows[-1].update(update)
            with self.assertRaises(ValueError): verify_observations(rows)


if __name__=='__main__': unittest.main(verbosity=2)
