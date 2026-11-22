"""Budgets shared by the Lua host and its isolated child process."""
from dataclasses import dataclass
import math


@dataclass(frozen=True)
class Limits:
    seconds: float = 60
    calls: int = 64
    instructions: int = 1_000_000
    memory: int = 16_000_000
    cleanup_seconds: float = 3
    actions: int = 8

    def validate(self):
        for value, low, high in ((self.seconds, .05, 300), (self.calls, 1, 256),
                                 (self.instructions, 100, 10_000_000),
                                 (self.memory, 100_000, 64_000_000),
                                 (self.cleanup_seconds, .05, 10)):
            if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
                raise ValueError('Invalid trusted Lua limit')
        if any(type(v) is not int for v in (self.calls, self.instructions, self.memory)):
            raise ValueError('Integer budgets required')
        if type(self.actions) is not int or not 0 <= self.actions <= 32:
            raise ValueError('Action budget must be an integer in 0..32')
