# 10,000 this time to create plenty of space for items and locations
CHAR_OFFSET = 10000

# Maximum selected modded characters, including predefined and unnamed characters.
NUM_CUSTOM = 15

# These are permanent ID ranges, not counts of currently installed characters.
# Preserve the original vanilla and custom blocks when extending either roster.
ORIGINAL_VANILLA_BLOCKS = range(1, 6)
GENERIC_CHARACTER_BLOCKS = range(6, 21)
FUTURE_VANILLA_BLOCKS = range(21, 40)
PREDEFINED_CHARACTER_BLOCKS = range(40, 100)

ASCENSIONS = {
    'SwarmingElites': 'Disable Swarming Elites',
    'WearyTraveler': 'Disable Weary Traveler',
    'Poverty': 'Disable Poverty',
    'TightBelt': 'Disable Tight Belt',
    "AscendersBane": "Disable Ascender's Bane",
    'Inflation': 'Disable Inflation',
    'Scarcity': 'Disable Scarcity',
    'ToughEnemies': 'Disable Tough Enemies',
    'DeadlyEnemies': 'Disable Deadly Enemies',
    'DoubleBoss': 'Disable Double Boss',
}

ASCENSION_LIST = list(ASCENSIONS.keys())
