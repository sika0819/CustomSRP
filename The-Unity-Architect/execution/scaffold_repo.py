import os

def create_folders():
    # Framework centralized structure (v1.3.0)
    framework_path = 'The-Unity-Architect'
    framework_folders = [
        'skills',
        'execution',
        'Wiki/ADR',
        'Wiki/Systems',
        'Wiki/Features',
        'Wiki/Lore'
    ]
    
    for folder in framework_folders:
        full_path = os.path.join(framework_path, folder)
        os.makedirs(full_path, exist_ok=True)
        print(f"Created/Verified: {full_path}/")

    # Assets/_Project structure
    base_path = os.path.join('Assets', '_Project')
    project_folders = [
        'Scenes',
        'Scripts/Core',
        'Scripts/Gameplay',
        'Scripts/Characters',
        'Scripts/Net',
        'Scripts/UI',
        'Scripts/Audio',
        'Scripts/Tools',
        'Data/Abilities',
        'Data/Items',
        'Data/Buildings',
        'Data/Zones',
        'Data/Economy',
        'Prefabs',
        'Art',
        'VFX',
        'Materials',
        'Animations'
    ]

    for folder in project_folders:
        full_path = os.path.join(base_path, folder)
        os.makedirs(full_path, exist_ok=True)
        print(f"Created/Verified: {full_path}/")

if __name__ == '__main__':
    create_folders()
