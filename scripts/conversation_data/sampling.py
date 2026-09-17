"""Deterministic sampling masses, audited before GPU training can start."""
import collections
import numpy as np

CONFIG = dict(version=4, seed=42, batchSize=32, responsePool={'public': .6, 'authored': .4},
              history={'fresh': .25, 'short': .35, 'long': .40}, exactResponseCap=.005,
              semanticPools=['authored', 'focused', 'classification'], publicBatchFreezes='encoder_and_planner')


def history_bucket(row):
    size = row.get('packedHistoryTurns',len(row['turns']))
    return 'fresh' if size == 1 else 'short' if size <= 5 else 'long'


def weights(rows):
    eligible = [r for r in rows if r['training']['responseEligible']]
    if not eligible: raise ValueError('No eligible realization rows')
    pools = np.array([r['training']['pool'] for r in eligible])
    histories = np.array([history_bucket(r) for r in eligible])
    responses, inverse, counts = np.unique([r['response'] for r in eligible], return_inverse=True, return_counts=True)
    mass = 1 / counts[inverse].astype(float)
    mass /= mass.sum()
    groups = [(pools == key, target) for key, target in CONFIG['responsePool'].items()]
    groups += [(histories == key, target) for key, target in CONFIG['history'].items()]
    for mask, target in groups:
        capacity = len(set(inverse[mask])) * CONFIG['exactResponseCap']
        if capacity + 1e-12 < target:
            raise ValueError(f'Insufficient distinct responses: capacity {capacity:.4f} for required mass {target}')
    for _ in range(20000):
        for mask, target in groups:
            total = mass[mask].sum()
            if total == 0: raise ValueError('Required realization stratum is empty')
            mass[mask] *= target / total
        response_mass = np.bincount(inverse, weights=mass, minlength=len(responses))
        mass *= np.minimum(1, CONFIG['exactResponseCap'] / np.maximum(response_mass[inverse], 1e-300))
        if max(abs(mass[mask].sum()-target) for mask, target in groups) < 1e-10: break
    else:
        raise ValueError('Response cap and requested source/history marginals are not jointly feasible')
    result = {r['id']: float(w) for r, w in zip(eligible, mass)}
    report = dict(config=CONFIG, eligibleRows=len(eligible), distinctResponses=len(responses),
                  sourceMass={k: float(mass[pools == k].sum()) for k in CONFIG['responsePool']},
                  historyMass={k: float(mass[histories == k].sum()) for k in CONFIG['history']},
                  maximumExactResponseMass=float(np.bincount(inverse, weights=mass).max()),
                  mostWeightedResponses=sorted(((str(r),float(w)) for r,w in zip(responses,np.bincount(inverse,weights=mass))), key=lambda x:-x[1])[:20])
    return result, report
