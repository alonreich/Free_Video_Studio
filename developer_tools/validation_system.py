"""
Simple validation system for configuration validation.
Provides basic validation rules and feedback mechanism.
"""

from enum import Enum
from typing import Callable, Dict, Any, List, Optional

class ValidationLevel(Enum):
    """Validation severity levels."""
    INFO = "INFO"
    WARNING = "WARNING"
    ERROR = "ERROR"

class ValidationRule:
    """A validation rule with condition, message, and severity level."""
    
    def __init__(self, rule_id: str, condition: Callable[[], bool], 
                 message: str, level: ValidationLevel = ValidationLevel.ERROR):
        """
        Initialize a validation rule.
        Args:
            rule_id: Unique identifier for the rule
            condition: Callable that returns True if validation passes
            message: Message to display if validation fails
            level: Severity level of the rule
        """
        self.rule_id = rule_id
        self.condition = condition
        self.message = message
        self.level = level
    
    def check(self) -> Dict[str, Any]:
        """Check the rule and return validation result."""
        try:
            valid = self.condition()
            return {
                'valid': valid,
                'message': self.message,
                'level': self.level
            }
        except Exception as e:
            return {
                'valid': False,
                'message': f"Validation error in rule '{self.rule_id}': {e}",
                'level': ValidationLevel.ERROR
            }

class ValidationFeedback:
    """Manages validation rules and provides feedback."""
    
    def __init__(self):
        """Initialize validation feedback system."""
        self.rules: Dict[str, ValidationRule] = {}
    
    def add_rule(self, rule: ValidationRule) -> None:
        """Add a validation rule."""
        self.rules[rule.rule_id] = rule
    
    def check_all_rules(self, context: Optional[Dict[str, Any]] = None) -> Dict[str, Dict[str, Any]]:
        """
        Check all validation rules.
        Args:
            context: Optional context data for validation
        Returns:
            Dictionary mapping rule_id to validation result
        """
        results = {}
        for rule_id, rule in self.rules.items():
            results[rule_id] = rule.check()
        return results
    
    def get_issues(self, context: Optional[Dict[str, Any]] = None) -> List[str]:
        """
        Get list of validation issues.
        Args:
            context: Optional context data for validation
        Returns:
            List of validation issue messages
        """
        results = self.check_all_rules(context)
        issues = []
        for rule_id, result in results.items():
            if not result['valid']:
                issues.append(f"{result['level'].value}: {result['message']}")
        return issues
    
    def is_valid(self, context: Optional[Dict[str, Any]] = None) -> bool:
        """
        Check if all validation rules pass.
        Args:
            context: Optional context data for validation
        Returns:
            True if all rules pass, False otherwise
        """
        results = self.check_all_rules(context)
        return all(result['valid'] for result in results.values())