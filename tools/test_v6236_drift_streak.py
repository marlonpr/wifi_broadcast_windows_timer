#!/usr/bin/env python3
"""Portable drift scenarios using the C# source's actual streak predicate.

The estimator model is Python; this is not execution or type-checking of C#.
Native regressions are in SegmentDriftMonitorTests and ProductionSmoke.
"""
from pathlib import Path
import argparse
import re
import statistics

ROOT = Path(__file__).resolve().parents[1]


class DriftModel:
    def __init__(self, source):
        body = source.split('public sealed class SegmentDriftMonitor', 1)[1]
        match = re.search(r'int count =\s*(.*?)\s*\?\s*deviations.GetValueOrDefault\(item.Point.DeviceId\)\s*\+\s*1\s*:\s*0;', body, re.S)
        assert match, 'Streak predicate/increment/reset requires review'
        predicate = match[1].replace('Math.Abs(relative)', 'abs(relative)')
        predicate = predicate.replace('&&', ' and ').replace('||', ' or ').replace('!', 'not ')
        self.predicate = compile(predicate.strip(), '<C# streak predicate>', 'eval')
        self.reset()

    def reset(self):
        self.previous, self.reference, self.pending, self.streak = {}, {}, {}, {}

    def observe(self, points):
        rates = []
        for device, boot, build, master, disciplined in points:
            old = self.previous.get(device)
            if old and old[:2] == (boot, build) and master > old[2] and disciplined > old[3]:
                interval = master - old[2]
                rates.append((device, ((disciplined - old[3]) - interval) / interval * 1e6, interval))
            else:
                for state in (self.reference, self.pending, self.streak):
                    state.pop(device, None)
            self.previous[device] = (boot, build, master, disciplined)
        if len(rates) < 2:
            return {}
        median = statistics.median(rate for _, rate, _ in rates)
        result = {}
        for device, rate, interval in rates:
            relative = rate - median
            innovation = (relative - self.reference[device]) * interval / 1e6 if device in self.reference else None
            discontinuity = innovation is not None and abs(innovation) > 500
            rebaseline = discontinuity and device in self.pending and abs(relative - self.pending[device]) * interval / 1e6 <= 500
            if discontinuity and not rebaseline:
                self.pending[device] = relative
            else:
                self.pending.pop(device, None)
            over_limit = eval(self.predicate, {'__builtins__': {}}, {
                'abs': abs, 'relative': relative, 'discontinuity': discontinuity, 'rebaseline': rebaseline})
            self.streak[device] = self.streak.get(device, 0) + 1 if over_limit else 0
            if not discontinuity or rebaseline:
                self.reference[device] = relative
            result[device] = (relative, discontinuity, self.streak[device] >= 3)
        return result


def points(master, phase, boot=1, build='build'):
    return [('ESP01', boot, build, master, master + phase),
            ('ESP03', 1, 'build', master, master), ('ESP04', 1, 'build', master, master)]


def scenarios(source):
    model = DriftModel(source)
    for direction in (-1, 1):
        model.reset(); model.observe(points(1, 0)); model.observe(points(6_000_000_001, 0))
        phase = 0
        for step in range(1, 16):
            phase += direction * 600 * step
            relative, innovation, sustained = model.observe(points(6_000_000_001 + step * 6_000_000_000, phase))['ESP01']
            assert abs(relative - direction * step * 0.1) < 1e-9
            assert innovation and sustained == (step >= 13), (direction, step, relative, sustained)
        print(f'PASS: {direction:+d} ramp; innovation persists; sustained at 1.1/1.2/1.3 ppm')
    model.reset(); model.observe(points(1, 0)); model.observe(points(540_000_001, 0))
    for step in range(1, 5):
        _, innovation, sustained = model.observe(points(540_000_001 + step * 540_000_000, step * 810))['ESP01']
        assert innovation == (step <= 2) and sustained == (step >= 3)
    print('PASS: 1.5 ppm step; rebaseline retained; third high estimate alerts')
    model.reset(); model.observe(points(1, 0)); model.observe(points(540_000_001, 0))
    for epoch, phase in [(1_080_000_001, 10000), (1_620_000_001, 0), (2_160_000_001, 0)]:
        assert not any(item[2] for item in model.observe(points(epoch, phase)).values())
    print('PASS: single corrupted sync epoch affects two intervals without sustained warning')
    model.reset(); model.observe(points(1, 0)); epoch, phase = 1, 0
    for index, increment in enumerate((810, 810, 540, 810, 810, 810)):
        epoch += 540_000_000; phase += increment
        assert model.observe(points(epoch, phase))['ESP01'][2] == (index == 5)
    print('PASS: exactly 1 ppm resets the >1 ppm consecutive streak')
    for change in ('boot', 'build', 'reset'):
        model.reset(); model.observe(points(1, 0)); model.observe(points(540_000_001, 810)); model.observe(points(1_080_000_001, 1620))
        boot, build = (2 if change == 'boot' else 1), ('other' if change == 'build' else 'build')
        if change == 'reset': model.reset()
        assert 'ESP01' not in model.observe(points(1_620_000_001, 2430, boot, build))
        assert not model.observe(points(2_160_000_001, 3240, boot, build))['ESP01'][2]
    print('PASS: reboot/build change/reset discard the streak')
    print('DRIFT_SCENARIO_MODEL=PASS (source-derived streak predicate; native .NET tests still required)')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=ROOT/'windows-controller/FactoryTimer.Controller.Core/SegmentedPrograms.cs')
    scenarios(parser.parse_args().source.read_text())
